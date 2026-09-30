using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Replay;

internal static class ReplayCommands
{
    internal const string BetaFeatureNotice =
        "BETA FEATURE: Session replay is experimental and best-effort. Supervise the target and be prepared to stop unintended actions.";
    private static readonly string[] replayFlagOptions =
    [
        "help",
        "headless",
        "beta",
        "json",
        "silent",
        "verbose",
        "enable-repository-automations",
        "plan-only",
        "allow-sparse-frames",
        "stop-on-failure"
    ];
    private static readonly string[] replayValueOptions =
    [
        "app-id",
        "annotation",
        "start",
        "end",
        "target-session-id",
        "session-id",
        "device-id",
        "device",
        "platform",
        "device-kind",
        "app",
        "ipa",
        "application-path",
        "wait-seconds",
        "team-id",
        "reasoning",
        "model",
        "model-transport",
        "max-turns",
        "max-round-trips",
        "max-tool-calls",
        "secret",
        "repository",
        "organization",
        "project",
        "host",
        "token-env",
        "data-dir",
        "adb-path",
        "xcode-path",
        "secret-store-file",
        "secret-key-file",
        "discovery-port",
        "websocket-port",
        "automation-repository",
        "node-path"
    ];

    internal static void ValidateOptions(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        arguments.EnsureOptionContract("ansight replay", replayFlagOptions, replayValueOptions);
    }

    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        ValidateOptions(arguments);
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        arguments.EnsurePositionalCount(
            3,
            "ansight replay ansight|sentry|posthog <session-id|recording-id|export.json> [options]");
        var sourceKind = arguments.RequirePositional(1, "replay source").ToLowerInvariant();
        var source = arguments.RequirePositional(2, "replay source identifier or export file");
        if (sourceKind is not ("ansight" or "sentry" or "posthog" or "post-hog"))
        {
            throw new CliUsageException(
                $"Unknown replay source '{sourceKind}'. Expected ansight, sentry, or posthog.");
        }

        if (sourceKind is "sentry" or "posthog" or "post-hog")
        {
            if (arguments.GetOption("annotation") is not null)
            {
                throw new CliUsageException("--annotation is available only for Ansight session replay sources.");
            }

            using var loader = new ExternalReplaySourceLoader();
            var externalPlan = await loader.LoadAsync(
                    sourceKind,
                    source,
                    arguments.GetOption("app-id"),
                    arguments,
                    cancellationToken)
                .ConfigureAwait(false);
            if (arguments.HasFlag("plan-only"))
            {
                return WritePlanOnly(externalPlan, output);
            }

            return await ExecuteAsync(externalPlan, sourceSnapshot: null, arguments, output, cancellationToken)
                .ConfigureAwait(false);
        }

        var options = CliRuntime.ResolveOptions(arguments);
        AppSessionSnapshot snapshot;
        ReplayPlan plan;
        await using (var sourceLease = await CliRuntimeLease.CreateAsync(
                         options,
                         start: false,
                         cancellationToken).ConfigureAwait(false))
        {
            var loadedSnapshot = await sourceLease.Runtime.Sessions.LoadSnapshotAsync(source, null, cancellationToken)
                .ConfigureAwait(false);
            if (loadedSnapshot is null)
            {
                output.WriteError(
                    "session_not_found",
                    $"Source session '{source}' was not found.",
                    CliExitCodes.Failure);
                return CliExitCodes.Failure;
            }

            snapshot = loadedSnapshot;
            var replayRange = ResolveReplayTimelineRange(arguments, snapshot);
            plan = AnsightReplayPlanner.Build(snapshot, replayRange.StartUtc, replayRange.EndUtc);
            if (replayRange.AnnotationId is { } annotationId)
            {
                plan = plan with
                {
                    Diagnostics =
                    [
                        $"Replay range selected from annotation '{annotationId}'.",
                        .. plan.Diagnostics
                    ]
                };
            }
        }

        if (arguments.HasFlag("plan-only"))
        {
            return WritePlanOnly(plan, output);
        }

        return await ExecuteAsync(
                plan,
                snapshot,
                arguments,
                output,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> ExecuteAsync(
        ReplayPlan plan,
        AppSessionSnapshot? sourceSnapshot,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        output.WriteProgress(BetaFeatureNotice);
        if (plan.Steps.Count == 0)
        {
            output.WriteError(
                "replay_plan_empty",
                $"Replay source '{plan.SourceId}' contains no supported interaction steps.",
                CliExitCodes.Configuration);
            return CliExitCodes.Configuration;
        }

        if (plan.FrameCadence is { AllowsLiveReplay: false } cadence
            && !arguments.HasFlag("allow-sparse-frames"))
        {
            output.WriteError(
                "replay_frame_support_insufficient",
                cadence.Message + " Live replay was not started. Capture the flow again with more frequent "
                + "screenshots, inspect it with --plan-only, or explicitly continue with --allow-sparse-frames.",
                CliExitCodes.Configuration);
            return CliExitCodes.Configuration;
        }

        if (plan.FrameCadence is { } allowedCadence)
        {
            var isOverride = !allowedCadence.AllowsLiveReplay;
            var progressStage = isOverride || string.Equals(
                allowedCadence.Rating,
                "degraded",
                StringComparison.Ordinal)
                ? "source.warning"
                : "source.cadence";
            output.WriteProgress(
                $"[{progressStage}] "
                + (isOverride ? "Sparse-frame override active. " : string.Empty)
                + allowedCadence.Message);
        }

        await using (var lease = await CliRuntimeLease.CreateAsync(
                         CliRuntime.ResolveOptions(arguments),
                         start: true,
                         cancellationToken).ConfigureAwait(false))
        {
            var targetResolution = await ResolveTargetSessionAsync(
                    lease.Runtime,
                    plan.AppId,
                    sourceSnapshot,
                    arguments,
                    output,
                    arguments.GetOption("target-session-id") ?? arguments.GetOption("session-id"),
                    cancellationToken)
                .ConfigureAwait(false);
            var target = targetResolution.Session;
            plan = plan with { AppId = target.AppId };
            if (arguments.GetOption("repository") is { } repositoryPath)
            {
                var workspace = lease.Runtime.Apps.ConfigureRepositoryWorkspace(
                    target.AppId,
                    Path.GetFullPath(repositoryPath));
                if (!workspace.IsSuccess)
                {
                    output.WriteError("configuration", workspace.Message, CliExitCodes.Configuration);
                    return CliExitCodes.Configuration;
                }
            }

            var sessionTagging = await UpdateReplaySessionTagsAsync(
                    lease.Runtime,
                    target.SessionId,
                    plan.SourceKind,
                    "running",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sessionTagging.IsSuccess)
            {
                output.WriteError("session_tagging_failed", sessionTagging.Message, CliExitCodes.Configuration);
                return CliExitCodes.Configuration;
            }

            var progress = new CliProgress<SimulatorAgentProgress>(value =>
            {
                if (arguments.IsVerbose
                    || value.Stage is not (SimulatorAgentProgressStage.Thinking
                        or SimulatorAgentProgressStage.ModelCompleted))
                {
                    output.WriteProgress(SimulatorAgentProgressFormatter.Format(value));
                }
            });
            var reasoning = ReasoningOptions.Resolve(arguments);
            var model = ReasoningOptions.ResolveModelOverride(arguments);
            var requestedMaximumRoundTrips = arguments.GetIntOption("max-round-trips", 64, 1, 500);
            var preparation = await PrepareReplayRunAsync(
                    lease.Runtime.WorkspaceTests.RunGateway,
                    plan,
                    target.AppId,
                    model,
                    arguments.GetOption("repository") is { } preparationRepository
                        ? Path.GetFullPath(preparationRepository)
                        : Path.Combine(lease.Runtime.BaseFolderPath, "replays"),
                    WorkspaceTestTeamOptions.Resolve(arguments),
                    cancellationToken,
                    reasoning)
                .ConfigureAwait(false);
            if (preparation.HasResolvedTeam)
            {
                output.WriteProgress(
                    $"[organisation.selected] Hosted replay execution resolved to '{preparation.TeamName}' ({preparation.TeamId:D}).");
            }
            if (!preparation.IsSuccess)
            {
                await UpdateReplaySessionTagsAsync(
                        lease.Runtime,
                        target.SessionId,
                        plan.SourceKind,
                        "failed",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                output.WriteError("configuration", preparation.Message, CliExitCodes.Configuration);
                return CliExitCodes.Configuration;
            }

            var request = CreateReplayRunRequest(
                plan,
                target,
                arguments,
                preparation,
                requestedMaximumRoundTrips,
                targetResolution.LaunchedTarget?.DeviceIdentifier);

            SimulatorAgentRunResult? result = null;
            var completionStatus = "failed";
            var runStopwatch = Stopwatch.StartNew();
            try
            {
                result = await lease.Runtime.SimulatorAgent.RunAsync(request, progress, cancellationToken)
                    .ConfigureAwait(false);
                completionStatus = result.Status.ToString().ToLowerInvariant();
            }
            catch (InvalidOperationException exception)
            {
                await UpdateReplaySessionTagsAsync(
                        lease.Runtime,
                        target.SessionId,
                        plan.SourceKind,
                        "failed",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                output.WriteError("configuration", exception.Message, CliExitCodes.Configuration);
                return CliExitCodes.Configuration;
            }
            catch (OperationCanceledException)
            {
                completionStatus = "cancelled";
                await UpdateReplaySessionTagsAsync(
                        lease.Runtime,
                        target.SessionId,
                        plan.SourceKind,
                        "cancelled",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch
            {
                await UpdateReplaySessionTagsAsync(
                        lease.Runtime,
                        target.SessionId,
                        plan.SourceKind,
                        "failed",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                runStopwatch.Stop();
                var meteringWarning = await CompleteReplayRunAsync(
                        lease.Runtime.WorkspaceTests.RunGateway,
                        preparation,
                        result,
                        completionStatus,
                        runStopwatch.ElapsedMilliseconds)
                    .ConfigureAwait(false);
                if (meteringWarning is not null)
                {
                    output.WriteProgress($"[metering.warning] {meteringWarning}");
                }
            }

            sessionTagging = await UpdateReplaySessionTagsAsync(
                    lease.Runtime,
                    target.SessionId,
                    plan.SourceKind,
                    result!.Status.ToString().ToLowerInvariant(),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var response = new ReplayRunOutput(
                "ansight.replay-run/v1",
                DateTimeOffset.UtcNow,
                PlanOnly: false,
                BetaFeatureNotice,
                plan,
                target.SessionId,
                target.AppId,
                result,
                sessionTagging);
            output.Write(response, () => RenderRun(response));
            return result.Status switch
            {
                SimulatorAgentRunStatus.Succeeded => CliExitCodes.Success,
                SimulatorAgentRunStatus.Cancelled => CliExitCodes.Cancelled,
                _ => CliExitCodes.TestFailed
            };
        }
    }

    internal static SimulatorAgentRunRequest CreateReplayRunRequest(
        ReplayPlan plan,
        AppSessionSnapshot target,
        CliArguments arguments,
        WorkspaceTestRunPreparation preparation,
        int requestedMaximumRoundTrips,
        string? launchedDeviceIdentifier = null)
    {
        var maximumRoundTrips = Math.Min(
            requestedMaximumRoundTrips,
            preparation.MaximumRoundTrips ?? requestedMaximumRoundTrips);
        var maximumTurns = Math.Min(
            arguments.GetIntOption("max-turns", 64, 1, 512),
            maximumRoundTrips);
        var reasoningConfiguration = preparation.ReasoningConfiguration
                                     ?? AgentReasoningConfiguration.CreateDefault(
                                         ReasoningOptions.Resolve(arguments),
                                         ReasoningOptions.ResolveModelOverride(arguments));
        return new SimulatorAgentRunRequest(
            target.SessionId,
            plan.Instructions,
            reasoningConfiguration.Model,
            maximumTurns,
            arguments.GetIntOption("max-tool-calls", 512, 1, 4_000),
            target.AppId,
            ContinueAfterInstructionFailure: false)
        {
            Reasoning = reasoningConfiguration.Reasoning,
            ReasoningEffort = reasoningConfiguration.ReasoningEffort,
            ReasoningConfigurationRevision = reasoningConfiguration.Revision,
            MaximumRoundTrips = maximumRoundTrips,
            SecretAliases = arguments.GetOptions("secret"),
            ModelTransport = preparation.ModelTransport,
            StartupSteps = preparation.StartupSteps,
            TrackingRunId = preparation.TrackingRunId,
            TargetDeviceIdentifier = arguments.GetOption("device-id")
                                     ?? arguments.GetOption("device")
                                     ?? launchedDeviceIdentifier,
            WorkspacePath = arguments.GetOption("repository"),
            WorkspaceTestId = $"replay-{plan.SourceKind}",
            WorkspaceTestName = $"Replay {plan.SourceId}",
            CaptureTrace = arguments.HasFlag("trace"),
            OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments)
        };
    }

    internal static Task<WorkspaceTestRunPreparation> PrepareReplayRunAsync(
        IWorkspaceTestRunGateway? gateway,
        ReplayPlan plan,
        string appId,
        string model,
        string workspacePath,
        Guid? teamId,
        CancellationToken cancellationToken,
        string reasoning = AgentReasoningModes.Fast)
    {
        if (gateway is null)
        {
            return Task.FromResult(WorkspaceTestRunPreparation.Local());
        }

        return gateway.PrepareAsync(
            new WorkspaceTestRunPreparationRequest(
                teamId,
                workspacePath,
                $"replay-{plan.SourceKind}",
                $"Replay {plan.SourceId}",
                appId,
                model,
                ValidationAssertionCount: 0,
                InstructionCount: plan.Steps.Count,
                IsDefinition: false)
            {
                Reasoning = AgentReasoningModes.Normalize(reasoning)
            },
            cancellationToken);
    }

    private static async Task<string?> CompleteReplayRunAsync(
        IWorkspaceTestRunGateway? gateway,
        WorkspaceTestRunPreparation preparation,
        SimulatorAgentRunResult? result,
        string status,
        long durationMilliseconds)
    {
        if (gateway is null
            || !preparation.UsesExternalTransport
            || preparation.TrackingRunId is not { } trackingRunId)
        {
            return null;
        }

        try
        {
            var audit = result?.Audit;
            var completion = await gateway.CompleteAsync(
                    new WorkspaceTestRunMeterCompletion(
                        trackingRunId,
                        status,
                        audit?.DurationMilliseconds ?? durationMilliseconds,
                        audit?.InstructionCount ?? 0,
                        audit?.ModelPassCount ?? 0,
                        audit?.AnsightToolCallCount ?? 0,
                        audit?.SuccessfulAnsightToolCallCount ?? 0,
                        preparation.ModelTransport is not null ? audit?.Tokens : null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return completion.IsSuccess ? null : completion.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static async Task<ReplayTargetResolution> ResolveTargetSessionAsync(
        RuntimeCoordinator runtime,
        string? appId,
        AppSessionSnapshot? sourceSnapshot,
        CliArguments arguments,
        CliOutput output,
        string? requestedTargetSessionId,
        CancellationToken cancellationToken)
    {
        var sourceSessionId = sourceSnapshot?.SessionId;
        if (!string.IsNullOrWhiteSpace(requestedTargetSessionId))
        {
            var targetSessionId = requestedTargetSessionId.Trim();
            if (string.Equals(targetSessionId, sourceSessionId, StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    "The source and target Ansight sessions must be different. "
                    + "Connect a fresh app session and pass it with --target-session-id.");
            }

            var target = await runtime.Sessions.LoadSnapshotAsync(targetSessionId, null, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                throw new CliHostUnavailableException(
                    $"Target session '{targetSessionId}' was not found. Find a live target with 'ansight session list --connected'.");
            }

            if (!runtime.AppTools.IsConnected(target.SessionId))
            {
                throw new CliHostUnavailableException(
                    $"Target session '{target.SessionId}' is not connected. Replay execution requires a live app session.");
            }

            if (!string.IsNullOrWhiteSpace(appId)
                && !string.Equals(target.AppId, appId, StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    $"Target session '{target.SessionId}' belongs to '{target.AppId}', not replay app '{appId}'.");
            }

            return new ReplayTargetResolution(target, LaunchedTarget: null);
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new CliUsageException(
                "External replay execution requires --app-id unless --target-session-id selects a live app session.");
        }

        var summary = runtime.Sessions.GetSummaries()
            .Where(candidate => !string.Equals(candidate.SessionId, sourceSessionId, StringComparison.Ordinal))
            .Where(candidate => string.Equals(candidate.AppId, appId, StringComparison.Ordinal))
            .Where(candidate => runtime.AppTools.IsConnected(candidate.SessionId))
            .OrderByDescending(static candidate => candidate.LastUpdatedUtc)
            .FirstOrDefault();
        if (summary is not null)
        {
            var connectedTarget = await runtime.Sessions.LoadSnapshotAsync(
                    summary.SessionId,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (connectedTarget is not null)
            {
                return new ReplayTargetResolution(connectedTarget, LaunchedTarget: null);
            }
        }

        var targetRequest = ResolveReplayTargetRequest(arguments, sourceSnapshot);
        var progress = new CliProgress<WorkspaceTestRunProgress>(value =>
            output.WriteProgress($"[{value.Stage}] {value.Message}"));
        var launcher = new WorkspaceTestTargetLauncher(runtime.Devices, runtime.Pairing);
        var launchStartedUtc = DateTimeOffset.UtcNow;
        var launch = await launcher.LaunchAsync(
                appId,
                targetRequest,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!launch.IsSuccess || launch.Target is null)
        {
            throw new CliHostUnavailableException(
                $"Ansight could not provision a replay target for '{appId}'. {launch.Message}");
        }

        var waitTimeout = arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45));
        output.WriteProgress(
            $"[session.wait] Waiting up to {waitTimeout.TotalSeconds:N0} seconds for the launched app to connect to Ansight.");
        AppSessionSnapshot? launchedSession;
        try
        {
            launchedSession = await WaitForLaunchedSessionAsync(
                    runtime,
                    appId,
                    sourceSessionId,
                    launch.Target,
                    launchStartedUtc,
                    waitTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            runtime.Pairing.RevokeUnconsumedEnrollment(launch.EnrollmentInviteId, appId);
        }

        if (launchedSession is null)
        {
            throw new CliHostUnavailableException(
                $"Ansight launched '{appId}' on '{launch.Target.DeviceName}', but the app did not connect within "
                + $"{waitTimeout.TotalSeconds:N0} seconds. Confirm the installed build starts the Ansight SDK, "
                + "or select another target with --device-id.");
        }

        output.WriteProgress(
            $"[session.selected] Using newly connected session '{launchedSession.SessionId}' on '{launch.Target.DeviceName}'.");
        return new ReplayTargetResolution(launchedSession, launch.Target);
    }

    internal static WorkspaceTestTargetRequest? ResolveReplayTargetRequest(
        CliArguments arguments,
        AppSessionSnapshot? sourceSnapshot)
    {
        var requested = WorkspaceTestTargetOptions.Resolve(arguments);
        if (requested?.ExecutionMode == WorkspaceExecutionModes.Device)
            throw new CliUsageException("Device execution v1 supports app execute and UI prompt tests; replay execution requires SDK mode.");
        var platform = requested?.Platform ?? ResolveSourcePlatform(sourceSnapshot);
        var deviceIdentifier = requested?.DeviceIdentifier
                               ?? (sourceSnapshot is null
                                   ? null
                                   : DeviceLifecycleTool.ResolveNativeDeviceIdentifier(sourceSnapshot));
        var applicationPath = requested?.ApplicationPath;
        var deviceKind = requested?.DeviceKind;
        var headless = requested?.Headless ?? false;
        return string.IsNullOrWhiteSpace(platform)
               && string.IsNullOrWhiteSpace(deviceIdentifier)
               && string.IsNullOrWhiteSpace(applicationPath)
               && string.IsNullOrWhiteSpace(deviceKind)
               && !headless
            ? null
            : new WorkspaceTestTargetRequest(platform, deviceIdentifier, applicationPath, deviceKind, headless);
    }

    private static async Task<AppSessionSnapshot?> WaitForLaunchedSessionAsync(
        RuntimeCoordinator runtime,
        string appId,
        string? sourceSessionId,
        WorkspaceTestTarget launchedTarget,
        DateTimeOffset launchStartedUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var boundedTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
        var timeoutAtUtc = DateTimeOffset.UtcNow.Add(boundedTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summary = runtime.Sessions.GetSummaries()
                .Where(candidate => !string.Equals(candidate.SessionId, sourceSessionId, StringComparison.Ordinal))
                .Where(candidate => string.Equals(candidate.AppId, appId, StringComparison.OrdinalIgnoreCase))
                .Where(candidate => runtime.AppTools.IsConnected(candidate.SessionId))
                .Where(candidate => MatchesLaunchedTarget(candidate, launchedTarget, launchStartedUtc))
                .OrderByDescending(static candidate => candidate.LastUpdatedUtc)
                .FirstOrDefault();
            if (summary is not null)
            {
                return await runtime.Sessions.LoadSnapshotAsync(summary.SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (DateTimeOffset.UtcNow >= timeoutAtUtc)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool MatchesLaunchedTarget(
        AppSessionSnapshot candidate,
        WorkspaceTestTarget target,
        DateTimeOffset launchStartedUtc)
    {
        var nativeDeviceIdentifier = DeviceLifecycleTool.ResolveNativeDeviceIdentifier(candidate);
        if (string.Equals(nativeDeviceIdentifier, target.DeviceIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!DeviceKinds.IsPhysical(target.DeviceKind)
            || (candidate.CreatedUtc < launchStartedUtc
                && !(candidate.AppStateChangedUtc >= launchStartedUtc))
            || candidate.DeviceProfile?.Device is not { } device
            || device.IsVirtual == true
            || device.IsEmulator == true
            || string.IsNullOrWhiteSpace(device.OsName))
        {
            return false;
        }

        return target.Platform switch
        {
            DevicePlatforms.Ios => device.OsName.Contains("ios", StringComparison.OrdinalIgnoreCase),
            DevicePlatforms.Android => device.OsName.Contains("android", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static string? ResolveSourcePlatform(AppSessionSnapshot? sourceSnapshot)
    {
        var osName = sourceSnapshot?.DeviceProfile?.Device?.OsName
                     ?? TryReadDeviceProfileString(sourceSnapshot?.DeviceProfileJson, "osName");
        if (string.IsNullOrWhiteSpace(osName))
        {
            return null;
        }

        if (osName.Contains("ios", StringComparison.OrdinalIgnoreCase))
        {
            return DevicePlatforms.Ios;
        }

        return osName.Contains("android", StringComparison.OrdinalIgnoreCase)
            ? DevicePlatforms.Android
            : null;
    }

    private static string? TryReadDeviceProfileString(string? profileJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(profileJson);
            return document.RootElement.TryGetProperty("device", out var device)
                   && device.ValueKind == JsonValueKind.Object
                   && device.TryGetProperty(propertyName, out var property)
                   && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int WritePlanOnly(ReplayPlan plan, CliOutput output)
    {
        var response = new ReplayRunOutput(
            "ansight.replay-run/v1",
            DateTimeOffset.UtcNow,
            PlanOnly: true,
            BetaFeatureNotice,
            plan,
            TargetSessionId: null,
            TargetAppId: plan.AppId,
            Result: null,
            SessionTagging: null);
        output.Write(response, () => RenderPlan(plan));
        return plan.Steps.Count > 0 ? CliExitCodes.Success : CliExitCodes.Configuration;
    }

    private static string RenderRun(ReplayRunOutput response)
    {
        var result = response.Result!;
        var tagging = response.SessionTagging!;
        return $"*** {BetaFeatureNotice} ***{Environment.NewLine}"
               + $"{result.Status.ToString().ToUpperInvariant()}: replayed {response.Plan.Steps.Count:N0} step(s) "
               + $"from {response.Plan.SourceKind} '{response.Plan.SourceId}' against session "
               + $"'{response.TargetSessionId}'.{Environment.NewLine}"
               + $"{result.Message}{Environment.NewLine}Run: {result.Audit.RunId}"
               + $"{Environment.NewLine}Session tags: {string.Join(", ", tagging.Tags)}"
               + (tagging.IsSuccess ? string.Empty : $"{Environment.NewLine}Tagging warning: {tagging.Message}")
               + RenderDiagnostics(response.Plan.Diagnostics);
    }

    private static string RenderPlan(ReplayPlan plan)
    {
        var lines = new List<string>
        {
            $"*** {BetaFeatureNotice} ***",
            $"Replay plan: {plan.SourceKind} '{plan.SourceId}' · {plan.Steps.Count:N0} step(s)"
        };
        if (plan.FrameCadence is { } cadence)
        {
            lines.Add($"Frame support: {cadence.Rating.ToUpperInvariant()} · {cadence.Message}");
        }

        lines.AddRange(plan.Steps.Select(step => $"{step.Sequence}. [{step.Kind}] {step.Instruction}"));
        if (plan.Diagnostics.Count > 0)
        {
            lines.AddRange(plan.Diagnostics.Select(static diagnostic => $"Review: {diagnostic}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderDiagnostics(IReadOnlyList<string> diagnostics)
        => diagnostics.Count == 0
            ? string.Empty
            : Environment.NewLine
              + string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => $"Review: {diagnostic}"));

    internal static IReadOnlyList<string> BuildReplaySessionTags(
        IReadOnlyList<string> existingTags,
        string sourceKind,
        string status)
    {
        var tags = new SortedSet<string>(existingTags, StringComparer.OrdinalIgnoreCase);
        tags.RemoveWhere(static tag => tag.StartsWith("replay-status-", StringComparison.OrdinalIgnoreCase));
        tags.Add("replay-driven");
        tags.Add("replay-beta");
        tags.Add($"replay-source-{sourceKind.Trim().ToLowerInvariant()}");
        tags.Add($"replay-status-{status.Trim().ToLowerInvariant()}");
        return tags
            .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static tag => tag, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<ReplaySessionTaggingOutput> UpdateReplaySessionTagsAsync(
        RuntimeCoordinator runtime,
        string sessionId,
        string sourceKind,
        string status,
        CancellationToken cancellationToken)
    {
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return new ReplaySessionTaggingOutput(
                false,
                $"Target session '{sessionId}' was unavailable while applying replay provenance.",
                []);
        }

        var tags = BuildReplaySessionTags(snapshot.Tags, sourceKind, status);
        var result = runtime.SessionEditing.UpdateMetadata(
            sessionId,
            snapshot.IsPinned,
            tags,
            snapshot.Notes,
            snapshot.Name);
        return new ReplaySessionTaggingOutput(result.IsSuccess, result.Message, tags);
    }

    private static DateTimeOffset? ParseTimelinePosition(
        string? value,
        AppSessionSnapshot snapshot,
        string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('+'))
        {
            normalized = normalized[1..];
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0)
        {
            return snapshot.CreatedUtc.ToUniversalTime().AddSeconds(seconds);
        }

        throw new CliUsageException(
            $"--{optionName} must be an ISO-8601 timestamp or a non-negative seconds offset from session start.");
    }

    internal static ReplayTimelineRange ResolveReplayTimelineRange(
        CliArguments arguments,
        AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(snapshot);
        var annotationId = arguments.GetOption("annotation");
        if (string.IsNullOrWhiteSpace(annotationId))
        {
            return new ReplayTimelineRange(
                ParseTimelinePosition(arguments.GetOption("start"), snapshot, "start"),
                ParseTimelinePosition(arguments.GetOption("end"), snapshot, "end"),
                AnnotationId: null);
        }

        if (arguments.GetOption("start") is not null || arguments.GetOption("end") is not null)
        {
            throw new CliUsageException("--annotation cannot be combined with --start or --end.");
        }

        var normalizedAnnotationId = annotationId.Trim();
        var annotation = snapshot.Annotations.FirstOrDefault(candidate => string.Equals(
            candidate.AnnotationId,
            normalizedAnnotationId,
            StringComparison.Ordinal));
        if (annotation is null)
        {
            throw new CliUsageException(
                $"Annotation '{normalizedAnnotationId}' was not found in source session '{snapshot.SessionId}'.");
        }

        var startUtc = annotation.StartUtc.ToUniversalTime();
        var endUtc = (annotation.EndUtc ?? annotation.StartUtc).ToUniversalTime();
        foreach (var geometry in annotation.Geometry)
        {
            var capturedAtUtc = geometry.CapturedAtUtc.ToUniversalTime();
            if (capturedAtUtc < startUtc)
            {
                startUtc = capturedAtUtc;
            }

            if (capturedAtUtc > endUtc)
            {
                endUtc = capturedAtUtc;
            }
        }

        if (endUtc < startUtc)
        {
            var earlierUtc = endUtc;
            endUtc = startUtc;
            startUtc = earlierUtc;
        }

        if (endUtc <= startUtc)
        {
            endUtc = snapshot.LastUpdatedUtc.ToUniversalTime();
        }

        if (endUtc <= startUtc)
        {
            throw new CliUsageException(
                $"Annotation '{normalizedAnnotationId}' is at or after the end of source session '{snapshot.SessionId}'.");
        }

        return new ReplayTimelineRange(startUtc, endUtc, normalizedAnnotationId);
    }

    private static string BuildHelp()
        => """
           Reproduce recorded user flows through the bounded Ansight UI agent

           BETA FEATURE: Session replay is experimental. Supervise the target while it runs.

           Usage:
             ansight replay ansight <session-id> [--target-session-id <live-id>] [options]
             ansight replay sentry <export.json|replay-id> [options]
             ansight replay posthog <export.json|recording-id> [options]

           Source options:
             --app-id <id>              Application bundle/package ID for an external replay
             --annotation <id>          Replay an annotation range; point annotations replay to session end
             --start <value>            Ansight replay start: ISO-8601 or seconds from capture start
             --end <value>              Ansight replay end: ISO-8601 or seconds from capture start
             --plan-only                Print the derived steps without controlling a live app
             --allow-sparse-frames      Run when screenshot support is degraded or unreliable

           Live target options:
             --headless                Do not open simulator/emulator windows; shown by default
             --target-session-id <id>   Exact connected Ansight session to control
             --session-id <id>          Alias for --target-session-id
             --device-id <id>           Start and select an exact simulator/emulator/device
             --device <id>              Alias for --device-id
             --platform <ios|android>   Restrict automatic target selection
             --device-kind <kind>       Restrict selection to virtual or physical targets
             --app <path>               Install an .app or .apk before launch
             --ipa <path>               Install an .ipa before launch
             --application-path <path>  Generic alias for --app or --ipa
             --wait-seconds <count>     App-to-Ansight connection timeout; default: 45
             --team-id <uuid>           Select an organisation when more than one permits the app
             --reasoning <mode>      Reasoning mode: fast (default), balanced, or deep
             --model-transport <mode>         Model connection: auto (default), websocket, or http
             --max-turns <count>        Maximum model turns per step; default: 64
             --max-round-trips <count>  Maximum model round trips per step; default: 64
             --max-tool-calls <count>   Maximum tool calls for the run; default: 512
             --stop-on-failure          Stop after the first failed replay step (default)
             --secret <alias>           Expose a configured host-managed test secret (repeatable)
             --repository <path>        Expose trusted repository tasks to the replay agent
             --trace                    Capture the full exportable agent trace

           Provider download options:
             --organization <slug>      Sentry organization slug
             --project <id|slug>        Sentry project slug or PostHog project ID
             --host <url>               Provider base URL for regional or self-hosted deployments
             --token-env <name>         Environment variable containing the provider API token

           Local Sentry and PostHog JSON exports are parsed as RRWeb recordings. Sentry replay IDs
           use the documented recording-segments API. PostHog IDs use the snapshots endpoint used by
           recording export; exporting the recording to JSON is the most portable PostHog workflow.

           Ansight replay derives taps from touch-aligned visual trees and derives ordinary text
           replacement or clearing from visual-tree value changes. Secure/omitted values and
           keyboard-only commands still require secrets or explicit task instructions.

           Replay stops after the first failed step because subsequent steps depend on the app
           reaching the preceding recorded state.

           Replay is best-effort: masked inputs, missing DOM labels, unsupported gestures, and more
           than 50 actions are reported as plan diagnostics. If there is no connected app session,
           replay selects a compatible local target, starts it when needed, launches the app, and
           waits for a fresh Ansight session. Use target options when selection is ambiguous or the
           app must be installed. Runs use the hosted service.
           """;
}

internal sealed record ReplayTimelineRange(
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string? AnnotationId);
