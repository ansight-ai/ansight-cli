using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal static class AppExecutionService
{
    internal static readonly TimeSpan DefaultSessionWaitTimeout = TimeSpan.FromSeconds(45);
    internal static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAppExecuteAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var targets = ResolveTargets(arguments);
        var prompt = await ReadPromptAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (targets.Count > 1)
        {
            return await RunMultipleAsync(
                    arguments,
                    output,
                    targets,
                    [prompt],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await RunAsync(
                arguments,
                output,
                targets[0],
                [prompt],
                compatibilityOutput: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<int> RunInlineCompatibilityAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (WorkspaceTestTargetOptions.ResolveExecutionMode(arguments) == WorkspaceExecutionModes.Device)
            throw new CliUsageException("Device execution requires ansight app execute with --app or --app-id.");

        if (arguments.HasFlag("close-app-on-completion"))
        {
            throw new CliUsageException("--close-app-on-completion is supported by ansight app execute.");
        }

        arguments.EnsurePositionalCount(
            2,
            "ansight test run-inline --session-id <id> --instruction <text> [--instruction <text> ...]");
        var instructions = arguments.GetOptions("instruction").ToList();
        if (arguments.GetOption("instructions-file") is { } instructionsFile)
        {
            var fullPath = Path.GetFullPath(instructionsFile);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Instruction file '{fullPath}' was not found.");
            }

            instructions.AddRange((await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false))
                .Select(static line => line.Trim())
                .Where(static line => !string.IsNullOrWhiteSpace(line)));
        }

        if (instructions.Count == 0)
        {
            throw new CliUsageException(
                "Pass at least one --instruction <text> or --instructions-file <path>.");
        }

        return await RunAsync(
                arguments,
                output,
                new AppExecutionTarget(
                    arguments.RequireOption("session-id"),
                    ApplicationIdentifier: null,
                    LaunchRequest: null),
                instructions,
                compatibilityOutput: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<string> ReadPromptAsync(
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var inlinePrompt = arguments.GetOption("prompt");
        var promptFile = arguments.GetOption("prompt-file");
        if (inlinePrompt is not null && promptFile is not null)
        {
            throw new CliUsageException("Use only one of --prompt or --prompt-file.");
        }

        var prompt = inlinePrompt;
        if (promptFile is not null)
        {
            var fullPath = Path.GetFullPath(promptFile);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Prompt file '{fullPath}' was not found.");
            }

            prompt = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new CliUsageException("Pass one non-empty --prompt <text> or --prompt-file <path>.");
        }

        return prompt.Trim();
    }

    internal static AppExecutionTarget ResolveTarget(CliArguments arguments)
    {
        var targets = ResolveTargets(arguments);
        if (targets.Count > 1)
        {
            throw new CliUsageException(
                "Pass only one device identifier for this operation using --device-id or --device.");
        }

        return targets[0];
    }

    internal static IReadOnlyList<AppExecutionTarget> ResolveTargets(CliArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        const string usage = "ansight app execute [<session-id>] "
                             + "[--app <path>|--app-id <id> [--device-id <id>]] "
                             + "--prompt <text>|--prompt-file <path>";
        if (arguments.Positionals.Count is < 2 or > 3)
        {
            var message = arguments.Positionals.Count < 2
                ? $"Missing required arguments. Usage: {usage}"
                : $"Unexpected positional argument. Usage: {usage}";
            throw new CliUsageException(message);
        }

        var sessionId = arguments.Positionals.Count == 3
            ? arguments.RequirePositional(2, "session identifier")
            : null;
        if (sessionId is not null)
        {
            var deviceIdentifiers = arguments.GetOptions("device-id")
                .Concat(arguments.GetOptions("device"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (deviceIdentifiers.Length > 1)
            {
                throw new CliUsageException(
                    "A connected session can be bound to only one --device-id value.");
            }
            var launchOptions = new[]
            {
                "app-id",
                "app",
                "ipa",
                "application-path",
                "platform",
                "device-kind",
                "execution-mode",
                "wait-seconds"
            };
            var suppliedLaunchOption = launchOptions.FirstOrDefault(arguments.HasFlag);
            if (suppliedLaunchOption is not null)
            {
                throw new CliUsageException(
                    $"--{suppliedLaunchOption} launches an app and cannot be combined with a session ID. "
                    + "Remove the session ID to launch an app, or remove launch options to use the connected session.");
            }

            return [new AppExecutionTarget(
                sessionId,
                ApplicationIdentifier: null,
                LaunchRequest: null)];
        }

        var applicationIdentifier = arguments.HasFlag("app-id")
            ? arguments.RequireOption("app-id").Trim()
            : null;
        var launchRequests = WorkspaceTestTargetOptions.ResolveMany(arguments);
        var launchRequest = launchRequests.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(applicationIdentifier)
            && string.IsNullOrWhiteSpace(launchRequest?.ApplicationPath))
        {
            throw new CliUsageException(
                "Pass --app <path-to-.app-or-.apk> to install and launch a build, "
                + "or pass --app-id <id> to launch an installed app. "
                + "Use --device-id <id> to select an exact target.");
        }

        return launchRequests.Count == 0
            ? [new AppExecutionTarget(SessionId: null, applicationIdentifier, LaunchRequest: null)]
            : launchRequests.Select(request => new AppExecutionTarget(
                SessionId: null,
                applicationIdentifier,
                request)).ToArray();
    }

    internal static async Task<int> RunMultipleAsync(
        CliArguments arguments,
        CliOutput output,
        IReadOnlyList<AppExecutionTarget> targets,
        IReadOnlyList<string> instructions,
        CancellationToken cancellationToken)
    {
        var runtimeOptions = CliRuntime.ResolveOptions(arguments);
        var launchRequests = targets
            .Select(static target => target.LaunchRequest)
            .OfType<WorkspaceTestTargetRequest>()
            .ToArray();
        var options = await CliTestRuntimeOptionsResolver.ResolveAsync(
            runtimeOptions,
            launchRequests,
            cancellationToken).ConfigureAwait(false);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        using var commandContext = CliCommandContext.Current is null
            ? CliCommandContext.Push(
                lease.Runtime,
                options.DataDirectory,
                secretValue: null)
            : null;
        var startBarrier = new AppExecutionRunStartBarrier(targets.Count);
        var runTasks = targets.Select((target, index) => RunTargetAsync(
            target,
            index,
            startBarrier.CreateParticipant())).ToArray();
        var runs = await Task.WhenAll(runTasks).ConfigureAwait(false);
        foreach (var run in runs)
        {
            foreach (var diagnostic in run.Diagnostics)
            {
                output.WriteProgress($"[{run.DeviceIdentifier ?? "auto"}] {diagnostic}");
            }
        }

        var exitCode = runs.Any(static run => run.ExitCode == CliExitCodes.Cancelled)
            ? CliExitCodes.Cancelled
            : runs.All(static run => run.ExitCode == CliExitCodes.Success)
                ? CliExitCodes.Success
                : CliExitCodes.Failure;
        var response = new AppExecutionBatchOutput(
            "ansight.app-execution-batch/v1",
            DateTimeOffset.UtcNow,
            exitCode,
            runs);
        output.Write(response, () => RenderBatch(response));
        return exitCode;

        async Task<AppExecutionTargetRunOutput> RunTargetAsync(
            AppExecutionTarget target,
            int targetIndex,
            AppExecutionRunStartParticipant startParticipant)
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var childOutput = new CliOutput(
                json: true,
                standardOutput,
                standardError);
            int childExitCode;
            try
            {
                childExitCode = await RunAsync(
                    arguments,
                    childOutput,
                    target,
                    instructions,
                    compatibilityOutput: false,
                    cancellationToken,
                    startParticipant).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                startParticipant.Dispose();
                return new AppExecutionTargetRunOutput(
                    targetIndex + 1,
                    target.LaunchRequest?.DeviceIdentifier,
                    CliExitCodes.Failure,
                    exception.Message,
                    null,
                    []);
            }

            var outputText = standardOutput.ToString();
            AppExecutionOutput? execution = null;
            try
            {
                execution = JsonSerializer.Deserialize<AppExecutionOutput>(outputText, jsonOptions);
            }
            catch (JsonException)
            {
            }
            var message = execution?.Result.Message
                          ?? ReadOutputMessage(outputText)
                          ?? $"App execution exited with code {childExitCode}.";
            var diagnostics = standardError.ToString()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new AppExecutionTargetRunOutput(
                targetIndex + 1,
                target.LaunchRequest?.DeviceIdentifier,
                childExitCode,
                message,
                execution,
                diagnostics);
        }
    }

    internal static string RenderBatch(AppExecutionBatchOutput output)
    {
        var passedCount = output.Runs.Count(static run => run.ExitCode == CliExitCodes.Success);
        var lines = new List<string>
        {
            $"App execution complete: {passedCount:N0} passed, {output.Runs.Count - passedCount:N0} failed."
        };
        lines.AddRange(output.Runs.Select(run =>
            $"{(run.ExitCode == CliExitCodes.Success ? "PASSED" : "FAILED")}: "
            + $"{run.DeviceIdentifier ?? $"target-{run.TargetIndex}"}: {run.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string? ReadOutputMessage(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return output.Trim();
        }
    }

    internal static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        AppExecutionTarget target,
        IReadOnlyList<string> instructions,
        bool compatibilityOutput,
        CancellationToken cancellationToken,
        AppExecutionRunStartParticipant? startParticipant = null)
    {
        using var runStartParticipant = startParticipant;
        var options = CliRuntime.ResolveOptions(arguments);
        var launchSteps = new List<SimulatorAgentStartupStep>();
        var setupStartedUtc = DateTimeOffset.UtcNow;
        var setupTimer = Stopwatch.StartNew();
        var applicationIdentifier = target.ApplicationIdentifier;
        if (target.RequiresLaunch && string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            var resolution = await new AppExecutionArtifactIdentifierResolver().ResolveAsync(
                    target.LaunchRequest?.ApplicationPath
                    ?? throw new InvalidOperationException("An artifact path is required for identifier discovery."),
                    options.AdbPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.ApplicationIdentifier))
            {
                output.WriteError(
                    "application_id_not_resolved",
                    resolution.Message,
                    CliExitCodes.Configuration);
                return CliExitCodes.Configuration;
            }

            applicationIdentifier = resolution.ApplicationIdentifier;
            output.WriteProgress(
                $"[app.inspect] Identified artifact app ID '{applicationIdentifier}'.");
        }

        if (target.RequiresLaunch && string.IsNullOrWhiteSpace(target.ApplicationIdentifier))
            launchSteps.Add(new("Resolve application identifier", setupStartedUtc, setupTimer.ElapsedMilliseconds, "succeeded"));
        setupStartedUtc = DateTimeOffset.UtcNow;
        setupTimer.Restart();
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: true,
            cancellationToken).ConfigureAwait(false);
        launchSteps.Add(new("Start or connect to host runtime", setupStartedUtc, setupTimer.ElapsedMilliseconds, "succeeded"));
        await using var executionScope = new AppExecutionScope(
            arguments.HasFlag("close-app-on-completion"),
            lease.Runtime.Devices.TerminateApplicationAsync,
            output);
        AppSessionSnapshot snapshot;
        WorkspaceTestTarget? launchedTarget = null;
        if (target.RequiresLaunch)
        {
            var launchProgress = new CliProgress<WorkspaceTestRunProgress>(value =>
                output.WriteProgress($"[{value.Stage}] {value.Message}"));
            var launchResult = await lease.Runtime.WorkspaceTests.LaunchAppAndWaitForReservedSessionAsync(
                    applicationIdentifier!,
                    target.LaunchRequest,
                    requestedSessionId: null,
                    arguments.GetSecondsOption("wait-seconds", DefaultSessionWaitTimeout),
                    launchProgress,
                    cancellationToken,
                    onTargetLaunched: executionScope.SetTarget)
                .ConfigureAwait(false);
            if (!launchResult.IsSuccess || launchResult.Session is null)
            {
                output.WriteError(
                    "app_launch_failed",
                    launchResult.Message,
                    CliExitCodes.HostUnavailable);
                return CliExitCodes.HostUnavailable;
            }

            launchSteps.AddRange(launchResult.StartupSteps);
            snapshot = launchResult.Session;
            launchedTarget = launchResult.Target;
            executionScope.SessionClaim = launchResult.SessionClaim;
            executionScope.DeviceSession = launchResult.DeviceSession;
        }
        else
        {
            var sessionSnapshot = await lease.Runtime.Sessions.LoadSnapshotAsync(
                    target.SessionId!,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (sessionSnapshot is null)
            {
                output.WriteError(
                    "session_not_found",
                    $"Session '{target.SessionId}' was not found.",
                    CliExitCodes.Failure);
                return CliExitCodes.Failure;
            }

            snapshot = sessionSnapshot;
            if (!lease.Runtime.IsSessionLive(snapshot.SessionId))
            {
                var message = compatibilityOutput
                    ? $"Session '{snapshot.SessionId}' is not connected. Inline agent runs require a live app session."
                    : $"Session '{snapshot.SessionId}' is not connected. App execution requires a live app session.";
                output.WriteError(
                    "session_not_connected",
                    message,
                    CliExitCodes.HostUnavailable);
                return CliExitCodes.HostUnavailable;
            }

            executionScope.SessionClaim = lease.Runtime.WorkspaceTests.ReserveSession(snapshot);

            if (snapshot.CaptureSource == WorkspaceExecutionModes.Device)
            {
                var targetError = await lease.Runtime.WorkspaceTests.ValidateAttachedDeviceTargetAsync(snapshot,
                    new WorkspaceTestTargetRequest(DeviceIdentifier: arguments.GetOption("device-id") ?? arguments.GetOption("device")),
                    cancellationToken).ConfigureAwait(false);
                if (targetError is not null) throw new CliUsageException(targetError);
            }

            if (executionScope.CloseAppOnCompletion)
            {
                var inventory = await lease.Runtime.Devices.ListAsync(cancellationToken).ConfigureAwait(false);
                executionScope.SetTarget(AppExecutionScope.ResolveSessionTarget(
                    snapshot,
                    arguments.GetOption("device-id") ?? arguments.GetOption("device"),
                    inventory));
            }
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
        var requestedMaximumRoundTrips = arguments.GetIntOption("max-round-trips", 64, 1, 4_096);
        var preparation = await PrepareAppExecutionRunAsync(
                lease.Runtime.WorkspaceTests.RunGateway,
                snapshot.AppId,
                model,
                Path.Combine(lease.Runtime.BaseFolderPath, "app-executions"),
                WorkspaceTestTeamOptions.Resolve(arguments),
                instructions.Count,
                cancellationToken,
                reasoning)
            .ConfigureAwait(false);
        if (preparation.HasResolvedTeam)
        {
            output.WriteProgress(
                $"[organisation.selected] Hosted agent execution resolved to '{preparation.TeamName}' ({preparation.TeamId:D}).");
        }
        if (!preparation.IsSuccess)
        {
            output.WriteError("configuration", preparation.Message, CliExitCodes.Configuration);
            return CliExitCodes.Configuration;
        }

        var graphStoreMode = AppGraphStoreOptions.Resolve(arguments);
        var graphTeamId = graphStoreMode == AppGraphStoreMode.Local
            ? LocalAppGraphService.LocalTeamId
            : await ResolveHostedGraphTeamIdAsync(
                lease.Runtime,
                preparation.TeamId ?? WorkspaceTestTeamOptions.Resolve(arguments),
                snapshot.AppId,
                arguments.HasFlag("app-graph"),
                cancellationToken).ConfigureAwait(false);
        IAppGraphAgentRouteCatalog appGraphCatalog = graphStoreMode == AppGraphStoreMode.Local
            ? new LocalAppGraphAgentRouteCatalog(
                lease.Runtime.LocalAppGraphs,
                snapshot.AppId,
                snapshot.ClientName)
            : Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphAgentRouteCatalog>("Graph.CreateRouteCatalog", [lease.Runtime]);
        var appGraphDiscovery = await AppGraphAgentRouteDiscovery.DiscoverAsync(
                appGraphCatalog,
                graphTeamId,
                snapshot.AppId,
                instructions,
                isEnabled: arguments.HasFlag("app-graph"),
                requirePublished: graphStoreMode == AppGraphStoreMode.Hosted,
                cancellationToken)
            .ConfigureAwait(false);
        if (appGraphDiscovery.Plans.Count > 0)
        {
            output.WriteProgress(
                $"[route.plan] Loaded {appGraphDiscovery.Plans.Count} authorized published App Graph route candidate(s) for '{snapshot.AppId}'.");
        }
        if (arguments.IsVerbose || arguments.HasFlag("app-graph"))
        {
            foreach (var warning in appGraphDiscovery.Warnings)
            {
                output.WriteProgress($"[route.warning] {warning}");
            }
        }

        var maximumRoundTrips = Math.Min(
            requestedMaximumRoundTrips,
            preparation.MaximumRoundTrips ?? requestedMaximumRoundTrips);
        var maximumTurns = Math.Min(
            arguments.GetIntOption("max-turns", 64, 1, 512),
            maximumRoundTrips);
        var reasoningConfiguration = preparation.ReasoningConfiguration
                                     ?? AgentReasoningConfiguration.CreateDefault(reasoning, model);
        var request = new SimulatorAgentRunRequest(
                snapshot.SessionId,
                instructions,
                reasoningConfiguration.Model,
                maximumTurns,
                arguments.GetIntOption("max-tool-calls", 512, 1, 16_384),
                snapshot.AppId,
                !arguments.HasFlag("stop-on-failure"))
            {
                Reasoning = reasoningConfiguration.Reasoning,
                ReasoningEffort = reasoningConfiguration.ReasoningEffort,
                ReasoningConfigurationRevision = reasoningConfiguration.Revision,
                MaximumRoundTrips = maximumRoundTrips,
                SecretAliases = arguments.GetOptions("secret"),
                SecretResolver = ResolveSecret,
                ModelTransport = preparation.ModelTransport,
                StartupSteps = launchSteps.Concat(preparation.StartupSteps).ToArray(),
                TrackingRunId = preparation.TrackingRunId,
                TargetDeviceIdentifier = target.LaunchRequest?.DeviceIdentifier
                                         ?? arguments.GetOption("device-id")
                                         ?? arguments.GetOption("device")
                                         ?? launchedTarget?.DeviceIdentifier,
                WorkspacePath = Path.Combine(lease.Runtime.BaseFolderPath, "app-executions"),
                WorkspaceTestId = "app-execute",
                WorkspaceTestName = "App execution",
                AppGraphPlans = appGraphDiscovery.Plans,
                AppGraphEnabled = arguments.HasFlag("app-graph"),
                CaptureTrace = arguments.HasFlag("trace"),
                OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments)
            };

        SimulatorAgentRunResult? result = null;
        var completionStatus = "failed";
        var runStopwatch = Stopwatch.StartNew();
        try
        {
            if (runStartParticipant is not null)
            {
                output.WriteProgress(
                    $"[run.ready] Session '{snapshot.SessionId}' is ready; waiting for the other targets.");
                await runStartParticipant.ArriveAndWaitAsync(cancellationToken).ConfigureAwait(false);
            }
            result = await lease.Runtime.SimulatorAgent.RunAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
            completionStatus = result.Status.ToString().ToLowerInvariant();
        }
        catch (InvalidOperationException exception)
        {
            output.WriteError("configuration", exception.Message, CliExitCodes.Configuration);
            return CliExitCodes.Configuration;
        }
        catch (OperationCanceledException)
        {
            completionStatus = "cancelled";
            throw;
        }
        finally
        {
            runStopwatch.Stop();
            var meteringOutcome = await CompleteAppExecutionRunAsync(
                    lease.Runtime.WorkspaceTests.RunGateway,
                    preparation,
                    result,
                    completionStatus,
                    runStopwatch.ElapsedMilliseconds,
                    lease.Runtime.SimulatorAgent.PersistRunAudit)
                .ConfigureAwait(false);
            result = meteringOutcome.RunResult;
            if (meteringOutcome.Warning is not null)
            {
                output.WriteProgress($"[metering.warning] {meteringOutcome.Warning}");
            }
        }

        ArgumentNullException.ThrowIfNull(result);
        await executionScope.DisposeAsync().ConfigureAwait(false);
        WriteResult(output, snapshot, result, compatibilityOutput);
        return result.Status switch
        {
            SimulatorAgentRunStatus.Succeeded => CliExitCodes.Success,
            SimulatorAgentRunStatus.Cancelled => CliExitCodes.Cancelled,
            _ => compatibilityOutput ? CliExitCodes.TestFailed : CliExitCodes.Failure
        };
    }

    internal static Task<WorkspaceTestRunPreparation> PrepareAppExecutionRunAsync(
        IWorkspaceTestRunGateway? gateway,
        string appId,
        string model,
        string workspacePath,
        Guid? teamId,
        int instructionCount,
        CancellationToken cancellationToken,
        string reasoning = AgentReasoningModes.Fast)
    {
        if (gateway is null)
        {
            return Task.FromResult(WorkspaceTestRunPreparation.Failure(
                "Agent execution requires the Ansight cloud gateway. Sign in with 'ansight account login'."));
        }

        return gateway.PrepareAsync(
            new WorkspaceTestRunPreparationRequest(
                teamId,
                workspacePath,
                "app-execute",
                "App execution",
                appId,
                model,
                ValidationAssertionCount: 0,
                InstructionCount: instructionCount,
                IsDefinition: false)
            {
                Reasoning = AgentReasoningModes.Normalize(reasoning)
            },
            cancellationToken);
    }
internal static Task<Guid?> ResolveHostedGraphTeamIdAsync(
        RuntimeCoordinator runtime,
        Guid? requestedTeamId,
        string appId,
        bool isEnabled,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<Guid?>>("HostedAppCommands.ResolveHostedGraphTeamIdAsync", [runtime, requestedTeamId, appId, isEnabled, cancellationToken]);

    internal static async Task<AppExecutionMeteringOutcome> CompleteAppExecutionRunAsync(
        IWorkspaceTestRunGateway? gateway,
        WorkspaceTestRunPreparation preparation,
        SimulatorAgentRunResult? result,
        string status,
        long durationMilliseconds,
        Func<SimulatorAgentRunAudit, SimulatorAgentAuditPersistenceResult> persistAudit)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(persistAudit);
        if (gateway is null
            || !preparation.UsesExternalTransport
            || preparation.TrackingRunId is not { } trackingRunId)
        {
            return new AppExecutionMeteringOutcome(result, null);
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
                        preparation.ModelTransport is not null ? audit?.Tokens : null,
                        preparation.ModelTransport is not null ? audit?.CreateModelPassUsages() : null),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!completion.IsSuccess)
            {
                return new AppExecutionMeteringOutcome(result, completion.Message);
            }

            if (completion.Cost is not { } cost || result is null)
            {
                return new AppExecutionMeteringOutcome(result, null);
            }

            var costedAudit = result.Audit with { CalculatedCost = cost };
            var persistence = persistAudit(costedAudit);
            var costedResult = result with
            {
                Audit = costedAudit,
                AuditFilePath = persistence.FilePath ?? result.AuditFilePath,
                AuditPersistenceError = persistence.ErrorMessage ?? result.AuditPersistenceError
            };
            var persistenceWarning = string.IsNullOrWhiteSpace(persistence.ErrorMessage)
                ? null
                : $"Calculated AI cost was returned but could not be persisted: {persistence.ErrorMessage}";
            return new AppExecutionMeteringOutcome(costedResult, persistenceWarning);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                           or IOException
                                           or InvalidOperationException)
        {
            return new AppExecutionMeteringOutcome(result, exception.Message);
        }
    }

    internal static string? ResolveSecret(string alias)
        => CliCommandContext.Current?.ResolveSecret(alias)
           ?? CliCommandContext.ResolveScopedSecret(alias);

    internal static void WriteResult(
        CliOutput output,
        AppSessionSnapshot snapshot,
        SimulatorAgentRunResult result,
        bool compatibilityOutput)
    {
        var completedUtc = DateTimeOffset.UtcNow;
        string RenderResult(string runLabel)
            => $"{result.Status.ToString().ToUpperInvariant()}: {result.Message}{Environment.NewLine}"
               + $"{runLabel}: {result.Audit.RunId}{Environment.NewLine}"
               + $"Instructions: {result.Instructions.Count}; turns: {result.TotalTurns}; tool calls: {result.TotalToolCalls}"
               + $"{Environment.NewLine}Trace: {(result.Audit.TraceEnabled == true ? "captured" : "not captured; rerun with --trace")}"
               + (string.IsNullOrWhiteSpace(result.AuditFilePath)
                   ? string.Empty
                   : $"{Environment.NewLine}Audit: {result.AuditFilePath}");

        if (compatibilityOutput)
        {
            output.Write(
                new InlineAgentRunOutput(
                    "ansight.inline-agent-run/v1",
                    completedUtc,
                    snapshot.SessionId,
                    snapshot.AppId,
                    result),
                () => RenderResult("Run"));
            return;
        }

        output.Write(
            new AppExecutionOutput(
                "ansight.app-execution/v1",
                completedUtc,
                snapshot.SessionId,
                snapshot.AppId,
                result),
            () => RenderResult("Execution"));
    }
}

internal sealed record AppExecutionTarget(
    string? SessionId,
    string? ApplicationIdentifier,
    WorkspaceTestTargetRequest? LaunchRequest)
{
    public bool RequiresLaunch => SessionId is null;
}

internal sealed record AppExecutionOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    string SessionId,
    string AppId,
    SimulatorAgentRunResult Result);

internal sealed record AppExecutionMeteringOutcome(
    SimulatorAgentRunResult? RunResult,
    string? Warning);

internal sealed record AppExecutionTargetRunOutput(
    int TargetIndex,
    string? DeviceIdentifier,
    int ExitCode,
    string Message,
    AppExecutionOutput? Execution,
    IReadOnlyList<string> Diagnostics);

internal sealed record AppExecutionBatchOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    int ExitCode,
    IReadOnlyList<AppExecutionTargetRunOutput> Runs);

internal sealed record InlineAgentRunOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    string SessionId,
    string AppId,
    SimulatorAgentRunResult Result);

internal sealed class AppExecutionRunStartBarrier
{
    private readonly TaskCompletionSource release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int remainingParticipants;

    public AppExecutionRunStartBarrier(int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);
        remainingParticipants = participantCount;
    }

    public AppExecutionRunStartParticipant CreateParticipant()
        => new(this);

    internal void Arrive()
    {
        if (Interlocked.Decrement(ref remainingParticipants) == 0)
        {
            release.TrySetResult();
        }
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
        => release.Task.WaitAsync(cancellationToken);
}

internal sealed class AppExecutionRunStartParticipant(
    AppExecutionRunStartBarrier barrier) : IDisposable
{
    private int arrived;

    public async Task ArriveAndWaitAsync(CancellationToken cancellationToken)
    {
        Arrive();
        await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
        => Arrive();

    private void Arrive()
    {
        if (Interlocked.Exchange(ref arrived, 1) == 0)
        {
            barrier.Arrive();
        }
    }
}
