using Ansight.Host.Cloud.AppGraphs;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.AppGraph;

internal static class AppGraphCommands
{
    internal const int DefaultExploreMaximumActions = 1_024;
    internal const int MaximumExploreMaximumActions = 1_024;
    internal const int DefaultExploreMaximumTurns = 1_023;
    internal const int MaximumExploreMaximumTurns = 1_024;
    internal const int DefaultExploreMaximumRoundTrips = 1_024;
    internal const int MaximumExploreMaximumRoundTrips = 1_024;
    internal const int DefaultExploreMaximumToolCalls = 4_000;
    internal const int MaximumExploreMaximumToolCalls = 4_000;
    internal const int ExploreMaximumInstructionCharacters = 128_000;
    internal const int ExploreMaximumModelOutputTokens = 32_000;

    internal static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        if (CliCommandHelp.IsRequested(arguments))
        {
            return CliCommandHelp.Write(output, BuildHelp());
        }

        var action = arguments.Positionals.Count > 1
            ? arguments.RequirePositional(1, "App Graph action").ToLowerInvariant()
            : "list";
        ValidateArguments(arguments, action, positionalOffset: 1);
        var options = CliRuntime.ResolveOptions(arguments);
        await using var lease = await CliRuntimeLease.CreateAsync(
            options,
            start: action is "run" or "explore" or "sync",
            cancellationToken).ConfigureAwait(false);
        return await RunActionAsync(
            lease.Runtime,
            arguments,
            output,
            action,
            positionalOffset: 1,
            cancellationToken).ConfigureAwait(false);
    }

    public static Task<int> RunCloudAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
        => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("HostedGraphCommands.RunCloudAsync", [runtime, arguments, output, cancellationToken]);

    internal static void ValidateArguments(
        CliArguments arguments,
        string action,
        int positionalOffset)
    {
        if (action == "list")
        {
            arguments.EnsurePositionalCount(positionalOffset + 1, "ansight app-graph list --team-id <uuid>");
            if (AppGraphStoreOptions.Resolve(arguments) == AppGraphStoreMode.Hosted)
            {
                ParseRequiredGuidOption(arguments, "team-id", "organisation ID");
            }
            return;
        }

        if (action == "sync")
        {
            var direction = arguments.RequirePositional(positionalOffset + 1, "sync direction").ToLowerInvariant();
            if (direction is not ("push" or "pull"))
            {
                throw new CliUsageException("App Graph sync direction must be push or pull.");
            }
            arguments.EnsurePositionalCount(
                positionalOffset + 3,
                direction == "push"
                    ? "ansight app-graph sync push <local-graph-id> --team-id <uuid>"
                    : "ansight app-graph sync pull <hosted-graph-id> --app-id <id> [--published]");
            ParseGraphId(arguments.RequirePositional(positionalOffset + 2, "App Graph ID"));
            if (direction == "push")
            {
                ParseRequiredGuidOption(arguments, "team-id", "organisation ID");
            }
            else
            {
                _ = arguments.RequireOption("app-id");
            }
            return;
        }

        if (action == "explore")
        {
            var usage = "ansight app-graph explore [<app-id>] [--graph <name-or-id>|--new-graph <name>] [options]";
            if (arguments.Positionals.Count < positionalOffset + 1
                || arguments.Positionals.Count > positionalOffset + 2)
            {
                throw new CliUsageException($"Unexpected positional argument. Usage: {usage}");
            }

            var positionalAppId = arguments.Positionals.Count == positionalOffset + 2
                ? arguments.RequirePositional(positionalOffset + 1, "App ID")
                : null;
            var optionAppId = arguments.GetOption("app-id");
            if (positionalAppId is not null && optionAppId is not null)
            {
                throw new CliUsageException("Use either the positional App ID or --app-id, not both.");
            }
            if (string.IsNullOrWhiteSpace(positionalAppId) && string.IsNullOrWhiteSpace(optionAppId))
            {
                throw new CliUsageException(
                    $"No workspace app could be selected. Usage: {usage}");
            }
            if (arguments.HasFlag("graph")) _ = arguments.RequireOption("graph");
            if (arguments.HasFlag("new-graph")) _ = arguments.RequireOption("new-graph");
            if (arguments.HasFlag("graph") && string.IsNullOrWhiteSpace(arguments.RequireOption("graph")))
            {
                throw new CliUsageException("--graph requires a non-empty graph name or UUID.");
            }
            if (arguments.HasFlag("new-graph") && string.IsNullOrWhiteSpace(arguments.RequireOption("new-graph")))
            {
                throw new CliUsageException("--new-graph requires a non-empty graph name.");
            }
            if (arguments.HasFlag("graph") && arguments.HasFlag("new-graph"))
            {
                throw new CliUsageException("Use only one of --graph or --new-graph.");
            }
            if (arguments.HasFlag("new-graph") && arguments.HasFlag("published"))
            {
                throw new CliUsageException("A new exploration graph starts as a draft; do not combine --new-graph with --published.");
            }
            if (arguments.HasFlag("team-id"))
            {
                ParseRequiredGuidOption(arguments, "team-id", "organisation ID");
            }
            if (arguments.HasFlag("launch") && arguments.HasFlag("no-launch"))
            {
                throw new CliUsageException("Use only one of --launch or --no-launch.");
            }
            if (arguments.HasFlag("wait-seconds"))
            {
                _ = arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45));
            }
            return;
        }

        if (action is "show" or "plan" or "validate")
        {
            var usage = action switch
            {
                "show" => "ansight app-graph show <graph-id> [--published]",
                "plan" => "ansight app-graph plan <graph-id> [--target <node>] [--published]",
                _ => "ansight app-graph validate <graph-id> [--published]"
            };
            arguments.EnsurePositionalCount(
                positionalOffset + 2,
                usage);
            ParseGraphId(arguments.RequirePositional(positionalOffset + 1, "App Graph ID"));
            return;
        }

        if (action == "run")
        {
            arguments.EnsurePositionalCount(
                positionalOffset + 3,
                "ansight app-graph run <graph-id> <session-id> [--parameters <JSON>] [--published]");
            ParseGraphId(arguments.RequirePositional(positionalOffset + 1, "App Graph ID"));
            arguments.RequirePositional(positionalOffset + 2, "live session ID");
            return;
        }

        throw new CliUsageException(
            $"Unknown App Graph action '{action}'. Expected list, show, plan, validate, run, or explore.");
    }

    internal static async Task<int> RunActionAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        string action,
        int positionalOffset,
        CancellationToken cancellationToken)
    {
        if (action == "list")
        {
            arguments.EnsurePositionalCount(positionalOffset + 1, "ansight app-graph list --team-id <uuid>");
            return await ListAsync(runtime, arguments, output, cancellationToken).ConfigureAwait(false);
        }

        if (action == "sync")
        {
            return await SyncAsync(
                runtime,
                arguments.RequirePositional(positionalOffset + 1, "sync direction"),
                ParseGraphId(arguments.RequirePositional(positionalOffset + 2, "App Graph ID")),
                arguments,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        if (action == "explore")
        {
            return await ExploreAsync(
                runtime,
                arguments.Positionals.Count == positionalOffset + 2
                    ? arguments.RequirePositional(positionalOffset + 1, "App ID")
                    : arguments.RequireOption("app-id"),
                arguments,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        var graphId = ParseGraphId(arguments.RequirePositional(positionalOffset + 1, "App Graph ID"));
        if (action == "show")
        {
            arguments.EnsurePositionalCount(positionalOffset + 2, "ansight app-graph show <graph-id> [--published]");
            return await ShowAsync(runtime, graphId, arguments, output, cancellationToken).ConfigureAwait(false);
        }

        if (action == "plan")
        {
            arguments.EnsurePositionalCount(positionalOffset + 2, "ansight app-graph plan <graph-id> [--target <node>] [--published]");
            return await PlanAsync(runtime, graphId, arguments, output, cancellationToken).ConfigureAwait(false);
        }

        if (action == "validate")
        {
            arguments.EnsurePositionalCount(positionalOffset + 2, "ansight app-graph validate <graph-id> [--published]");
            return await ValidateAsync(runtime, graphId, arguments, output, cancellationToken).ConfigureAwait(false);
        }

        if (action == "run")
        {
            arguments.EnsurePositionalCount(positionalOffset + 3, "ansight app-graph run <graph-id> <session-id> [--parameters <JSON>] [--published]");
            return await RunGraphAsync(
                runtime,
                graphId,
                arguments.RequirePositional(positionalOffset + 2, "live session ID"),
                arguments,
                output,
                cancellationToken).ConfigureAwait(false);
        }

        throw new CliUsageException($"Unknown App Graph action '{action}'. Expected list, show, plan, validate, run, or explore.");
    }

    internal static async Task<int> ListAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var isLocal = AppGraphStoreOptions.Resolve(arguments) == AppGraphStoreMode.Local;
        var result = isLocal
            ? runtime.LocalAppGraphs.List(arguments.GetOption("app-id"), arguments.GetOption("search"))
            : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).ListAppGraphsAsync(
                ParseRequiredGuidOption(arguments, "team-id", "organisation ID"),
                arguments.GetOption("search"),
                cancellationToken).ConfigureAwait(false);
        output.Write(
            new AppGraphListOutput("ansight.app-graphs/v1", result),
            () => !result.IsSuccess
                ? result.Message
                : result.Graphs.Count == 0
                    ? isLocal
                        ? "No local App Graphs were found."
                        : "No App Graphs were found for this organisation."
                    : string.Join(
                        Environment.NewLine,
                        result.Graphs.Select(graph =>
                            $"{graph.Id:D}\tv{graph.Version}\t{graph.Status}\t{graph.Name}\t{graph.Intent}")));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> ShowAsync(
        RuntimeCoordinator runtime,
        Guid graphId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var result = await GetAppGraphAsync(
            runtime,
            graphId,
            arguments,
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new AppGraphDetailOutput("ansight.app-graph/v1", result),
            () => result.Detail is null
                ? result.Message
                : $"{result.Detail.Graph.Name}\n"
                  + (string.IsNullOrWhiteSpace(result.Detail.Graph.Intent) ? string.Empty : $"Description: {result.Detail.Graph.Intent}\n")
                  + $"Version: {result.Detail.Version.VersionNumber} ({result.Detail.Version.Status})\n"
                  + $"Bindings: {result.Detail.Bindings.Count}");
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> PlanAsync(
        RuntimeCoordinator runtime,
        Guid graphId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var detailResult = await GetAppGraphAsync(
            runtime,
            graphId,
            arguments,
            cancellationToken).ConfigureAwait(false);
        if (!detailResult.IsSuccess || detailResult.Detail is null)
        {
            return WriteDetailFailure(detailResult.Message, output);
        }

        var plan = AppGraphPlanner.Build(detailResult.Detail, arguments.GetOption("target"));
        output.Write(
            new AppGraphPlanOutput("ansight.app-graph-plan/v1", detailResult.Detail.Graph.Id, detailResult.Detail.Version.Id, plan),
            () => FormatPlan(detailResult.Detail, plan));
        return plan.Errors.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> ValidateAsync(
        RuntimeCoordinator runtime,
        Guid graphId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var detailResult = await GetAppGraphAsync(
            runtime,
            graphId,
            arguments,
            cancellationToken).ConfigureAwait(false);
        if (!detailResult.IsSuccess || detailResult.Detail is null)
        {
            return WriteDetailFailure(detailResult.Message, output);
        }

        var validation = AppGraphPlanner.Validate(detailResult.Detail);
        output.Write(
            new AppGraphValidationOutput(
                "ansight.app-graph-validation/v1",
                detailResult.Detail.Graph.Id,
                detailResult.Detail.Version.Id,
                validation),
            () => validation.Errors.Count == 0
                ? $"App Graph v{detailResult.Detail.Version.VersionNumber} is executable ({validation.EdgeCount} edges, {validation.BindingCount} bindings)."
                : "App Graph validation failed:" + Environment.NewLine
                  + string.Join(Environment.NewLine, validation.Errors.Select(error => $"- {error}")));
        return validation.Errors.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> RunGraphAsync(
        RuntimeCoordinator runtime,
        Guid graphId,
        string sessionId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            output.WriteError("session_not_found", $"Session '{sessionId}' was not found.", CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }
        if (!runtime.AppTools.IsConnected(snapshot.SessionId))
        {
            output.WriteError("session_not_connected", $"Session '{snapshot.SessionId}' is not connected. App Graph runs require a live app.", CliExitCodes.HostUnavailable);
            return CliExitCodes.HostUnavailable;
        }

        var detailResult = await GetAppGraphAsync(
            runtime,
            graphId,
            arguments,
            cancellationToken).ConfigureAwait(false);
        if (!detailResult.IsSuccess || detailResult.Detail is null)
        {
            return WriteDetailFailure(detailResult.Message, output);
        }

        var detail = detailResult.Detail;
        var isLocal = AppGraphStoreOptions.Resolve(arguments) == AppGraphStoreMode.Local;
        var plan = AppGraphPlanner.Build(detail, arguments.GetOption("target"));
        if (plan.Errors.Count > 0)
        {
            output.WriteError("app_graph_not_executable", string.Join(" ", plan.Errors), CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        var parameters = ReadParameters(arguments);
        var createRequest = new CloudAppGraphRunCreateRequest(
                detail.Graph.TeamId,
                detail.Graph.Id,
                detail.Version.Id,
                string.IsNullOrWhiteSpace(detail.Graph.Intent)
                    ? $"Execute an App Graph path in {detail.Graph.Name}."
                    : detail.Graph.Intent,
                parameters,
                snapshot.SessionId);
        var createResult = isLocal
            ? runtime.LocalAppGraphs.CreateRun(createRequest)
            : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).CreateAppGraphRunAsync(
                createRequest,
                cancellationToken).ConfigureAwait(false);
        if (!createResult.IsSuccess)
        {
            output.WriteError("app_graph_run_create_failed", createResult.Message, CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        var runId = createResult.RunId;
        var executedSteps = new List<AppGraphExecutedStep>();
        var traceIndex = 0;
        var failureMessage = string.Empty;
        try
        {
            foreach (var step in plan.Steps)
            {
                var edgeSucceeded = false;
                foreach (var binding in step.Bindings)
                {
                    var startedAt = DateTimeOffset.UtcNow;
                    var execution = await ExecuteBindingAsync(
                        runtime,
                        snapshot.SessionId,
                        snapshot.AppId,
                        isLocal ? null : detail.Graph.TeamId,
                        step,
                        binding,
                        parameters,
                        arguments,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    var completedAt = DateTimeOffset.UtcNow;
                    var status = execution.IsSuccess ? "succeeded" : "failed";
                    var writeRequest = new CloudAppGraphRunStepWriteRequest(
                            detail.Graph.TeamId,
                            runId,
                            traceIndex,
                            step.Edge.Id,
                            binding.Id,
                            status,
                            execution.Request,
                            execution.Response,
                            execution.Evidence,
                            execution.IsSuccess ? null : execution.Message,
                            startedAt,
                            completedAt);
                    var writeResult = isLocal
                        ? runtime.LocalAppGraphs.WriteRunStep(writeRequest)
                        : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).WriteAppGraphRunStepAsync(
                            writeRequest,
                            cancellationToken).ConfigureAwait(false);
                    executedSteps.Add(new AppGraphExecutedStep(traceIndex, step.Edge.Id, binding.Id, binding.Mechanism, status, execution.Message));
                    traceIndex++;
                    if (!writeResult.IsSuccess)
                    {
                        failureMessage = $"The action completed, but its evidence could not be recorded: {writeResult.Message}";
                        break;
                    }
                    if (execution.IsSuccess)
                    {
                        edgeSucceeded = true;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(failureMessage)) break;
                if (!edgeSucceeded)
                {
                    failureMessage = $"Every binding failed for edge '{step.Edge.Id}'.";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (isLocal)
            {
                runtime.LocalAppGraphs.CompleteRun(runId, "cancelled", "Run cancelled.");
            }
            else
            {
                await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).CompleteAppGraphRunAsync(runId, "cancelled", "Run cancelled.", CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            failureMessage = exception.GetBaseException().Message;
        }

        var succeeded = string.IsNullOrWhiteSpace(failureMessage);
        var message = succeeded
            ? $"Completed {plan.Steps.Count} semantic transitions and observed their postconditions."
            : failureMessage;
        var completeResult = isLocal
            ? runtime.LocalAppGraphs.CompleteRun(
                runId,
                succeeded ? "succeeded" : "failed",
                message)
            : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).CompleteAppGraphRunAsync(
                runId,
                succeeded ? "succeeded" : "failed",
                message,
                cancellationToken).ConfigureAwait(false);
        if (!completeResult.IsSuccess)
        {
            succeeded = false;
            message = $"{message} Run finalisation failed: {completeResult.Message}";
        }

        output.Write(
            new AppGraphRunOutput("ansight.app-graph-run/v1", runId, detail.Graph.Id, detail.Version.Id, succeeded, message, executedSteps),
            () => $"Run {runId:D}: {(succeeded ? "succeeded" : "failed")}\n{message}\n"
                  + string.Join(Environment.NewLine, executedSteps.Select(step => $"{step.StepIndex + 1}. {step.EdgeId} [{step.Mechanism}] {step.Status}")));
        return succeeded ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static async Task<int> ExploreAsync(
        RuntimeCoordinator runtime,
        string requestedAppId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var sessions = runtime.Sessions.GetSummaries();
        AppGraphExploreLiveTarget liveTarget;
        WorkspaceTestTarget? launchedTarget = null;
        try
        {
            liveTarget = AppGraphExploreTargetResolver.Resolve(
                requestedAppId,
                arguments.GetOption("session-id"),
                sessions,
                runtime.AppTools.GetConnectedSessionIds());
        }
        catch (CliHostUnavailableException) when (arguments.HasFlag("launch"))
        {
            var launchAppId = AppGraphExploreTargetResolver.ResolveAppIdForLaunch(
                requestedAppId,
                sessions);
            output.WriteProgress(
                $"[App Graph] No live session is connected for '{launchAppId}'; launching the app.");
            var launchProgress = new CliProgress<WorkspaceTestRunProgress>(value =>
                output.WriteProgress($"[App Graph] [{value.Stage}] {value.Message}"));
            var launchResult = await runtime.WorkspaceTests.LaunchAppAndWaitForSessionAsync(
                launchAppId,
                WorkspaceTestTargetOptions.Resolve(arguments),
                arguments.GetOption("session-id"),
                arguments.GetSecondsOption("wait-seconds", TimeSpan.FromSeconds(45)),
                launchProgress,
                cancellationToken).ConfigureAwait(false);
            if (!launchResult.IsSuccess || launchResult.Session is null)
            {
                output.WriteError(
                    "app_launch_failed",
                    launchResult.Message,
                    CliExitCodes.Failure);
                return CliExitCodes.Failure;
            }

            launchedTarget = launchResult.Target;
            liveTarget = new AppGraphExploreLiveTarget(
                launchResult.Session.AppId,
                launchResult.Session,
                UsedLegacySessionTarget: !launchAppId.Equals(
                    requestedAppId,
                    StringComparison.OrdinalIgnoreCase));
        }
        var snapshot = liveTarget.Session;
        var storeMode = AppGraphStoreOptions.Resolve(arguments);
        var cloudTarget = storeMode == AppGraphStoreMode.Local
            ? ResolveLocalExploreTarget(
                runtime,
                liveTarget.AppId,
                snapshot.ClientName,
                arguments)
            : await ResolveExploreCloudTargetAsync(
                runtime,
                liveTarget.AppId,
                snapshot.ClientName,
                arguments,
                cancellationToken).ConfigureAwait(false);

        var maximumActions = arguments.GetIntOption(
            "max-actions",
            DefaultExploreMaximumActions,
            1,
            MaximumExploreMaximumActions);
        var instruction = AppGraphExplorationCompiler.BuildInstruction(
            cloudTarget.Detail,
            cloudTarget.GraphName,
            maximumActions,
            arguments.GetOption("focus"));
        var reasoning = ReasoningOptions.Resolve(arguments);
        var model = ReasoningOptions.ResolveModelOverride(arguments);
        var workspacePath = Path.Combine(runtime.BaseFolderPath, "app-graph-executions");
        var preparation = await PrepareAppGraphAgentRunAsync(
                runtime.WorkspaceTests.RunGateway,
                snapshot.AppId,
                model,
                workspacePath,
                storeMode == AppGraphStoreMode.Local
                    ? null
                    : cloudTarget.RegisteredApp.TeamId == Guid.Empty
                    ? null
                    : cloudTarget.RegisteredApp.TeamId,
                "app-graph-explore",
                "App Graph exploration",
                cancellationToken,
                reasoning)
            .ConfigureAwait(false);
        if (preparation.HasResolvedTeam)
        {
            output.WriteProgress(
                $"[App Graph] [organisation.selected] Hosted agent execution resolved to '{preparation.TeamName}' ({preparation.TeamId:D}).");
        }
        if (!preparation.IsSuccess)
        {
            output.WriteError("configuration", preparation.Message, CliExitCodes.Configuration);
            return CliExitCodes.Configuration;
        }
        output.WriteProgress(
            $"[App Graph] Exploring app '{liveTarget.AppId}' through host-linked session '{snapshot.SessionId}'.");
        var progress = new CliProgress<SimulatorAgentProgress>(value =>
        {
            if (arguments.IsVerbose
                || value.Stage is not (SimulatorAgentProgressStage.Thinking or SimulatorAgentProgressStage.ModelCompleted))
            {
                output.WriteProgress($"[App Graph] [{value.Stage}] {value.Message}");
            }
        });

        SimulatorAgentRunResult? agentResult = null;
        var completionStatus = "failed";
        var runStopwatch = Stopwatch.StartNew();
        try
        {
            var request = ConfigureAppGraphAgentRequest(
                new SimulatorAgentRunRequest(
                    snapshot.SessionId,
                    [instruction],
                    model,
                    arguments.GetIntOption(
                        "max-turns",
                        DefaultExploreMaximumTurns,
                        1,
                        MaximumExploreMaximumTurns),
                    arguments.GetIntOption(
                        "max-tool-calls",
                        DefaultExploreMaximumToolCalls,
                        1,
                        MaximumExploreMaximumToolCalls),
                    snapshot.AppId,
                    ContinueAfterInstructionFailure: false)
                {
                    Reasoning = reasoning,
                    AppGraphExplorationName = cloudTarget.GraphName,
                    CaptureTrace = arguments.HasFlag("trace"),
                    MaximumInstructionCharacters = ExploreMaximumInstructionCharacters,
                    MaximumModelOutputTokens = ExploreMaximumModelOutputTokens,
                    OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments),
                    TargetDeviceIdentifier = arguments.GetOption("device-id")
                                             ?? arguments.GetOption("device")
                                             ?? launchedTarget?.DeviceIdentifier
                },
                preparation,
                arguments.GetIntOption(
                    "max-round-trips",
                    DefaultExploreMaximumRoundTrips,
                    1,
                    MaximumExploreMaximumRoundTrips),
                workspacePath,
                "app-graph-explore",
                "App Graph exploration");
            agentResult = await runtime.SimulatorAgent.RunAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false);
            completionStatus = agentResult.Status.ToString().ToLowerInvariant();
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
            var meteringOutcome = await AppExecutionService.CompleteAppExecutionRunAsync(
                    runtime.WorkspaceTests.RunGateway,
                    preparation,
                    agentResult,
                    completionStatus,
                    runStopwatch.ElapsedMilliseconds,
                    runtime.SimulatorAgent.PersistRunAudit)
                .ConfigureAwait(false);
            agentResult = meteringOutcome.RunResult;
            if (meteringOutcome.Warning is not null)
            {
                output.WriteProgress($"[App Graph] [metering.warning] {meteringOutcome.Warning}");
            }
        }

        ArgumentNullException.ThrowIfNull(agentResult);
        if (agentResult.Status != SimulatorAgentRunStatus.Succeeded)
        {
            output.WriteError(
                "app_graph_exploration_failed",
                $"{agentResult.Message} Audit: {agentResult.AuditFilePath ?? agentResult.Audit.RunId}",
                agentResult.Status == SimulatorAgentRunStatus.Cancelled ? CliExitCodes.Cancelled : CliExitCodes.Failure);
            return agentResult.Status == SimulatorAgentRunStatus.Cancelled ? CliExitCodes.Cancelled : CliExitCodes.Failure;
        }

        var completionSummary = agentResult.Instructions.FirstOrDefault()?.Summary;
        if (!AppGraphExplorationCompiler.TryCompile(completionSummary, out var candidate, out var compileError)
            || candidate is null)
        {
            runtime.SimulatorAgent.MarkAppGraphLiveRunFailed(
                agentResult.Audit.RunId,
                $"Exploration completed, but the graph could not be compiled: {compileError}");
            output.WriteError(
                "app_graph_exploration_invalid",
                $"{compileError} The agent audit is available at {agentResult.AuditFilePath ?? agentResult.Audit.RunId}.",
                CliExitCodes.Failure);
            return CliExitCodes.Failure;
        }

        var evidence = AppGraphExplorationCompiler.AttachAudit(candidate.Evidence, agentResult);
        var detail = cloudTarget.Detail;
        var graph = cloudTarget.Graph;
        var createdGraph = false;
        Guid? observationId = null;
        var saveMessage = "Exploration was not uploaded because --no-save was used.";
        var saveSucceeded = true;
        if (!arguments.HasFlag("no-save"))
        {
            if (cloudTarget.ShouldCreate)
            {
                var createResult = storeMode == AppGraphStoreMode.Local
                    ? runtime.LocalAppGraphs.Create(
                        snapshot.AppId,
                        snapshot.ClientName,
                        cloudTarget.GraphName,
                        BuildDefaultGraphIntent(cloudTarget.RegisteredApp.Name, arguments.GetOption("focus")),
                        candidate.Definition,
                        evidence)
                    : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).CreateAppGraphAsync(
                        new CloudAppGraphCreateRequest(
                            cloudTarget.RegisteredApp.TeamId,
                            cloudTarget.RegisteredApp.Id,
                            cloudTarget.GraphName,
                            BuildDefaultGraphIntent(cloudTarget.RegisteredApp.Name, arguments.GetOption("focus")),
                            candidate.Definition),
                        cancellationToken).ConfigureAwait(false);
                if (!createResult.IsSuccess || createResult.Detail is null)
                {
                    output.WriteError(
                        "app_graph_create_failed",
                        $"{createResult.Message} The completed exploration remains available in audit {agentResult.AuditFilePath ?? agentResult.Audit.RunId}.",
                        CliExitCodes.Failure);
                    return CliExitCodes.Failure;
                }

                detail = createResult.Detail;
                graph = detail.Graph;
                createdGraph = true;
            }

            if (graph is null)
            {
                output.WriteError(
                    "app_graph_not_resolved",
                    "The exploration completed, but no App Graph was available to receive its evidence.",
                    CliExitCodes.Failure);
                return CliExitCodes.Failure;
            }

            var observationRequest = new CloudAppGraphObservationCreateRequest(
                    graph.TeamId,
                    graph.Id,
                    snapshot.SessionId,
                    snapshot.AppId,
                    agentResult.Audit.RunId,
                    candidate.Definition,
                    evidence,
                    candidate.Confidence,
                    arguments.GetOption("notes"),
                    createdGraph ? detail?.Version.Id : null);
            var saveResult = storeMode == AppGraphStoreMode.Local
                ? runtime.LocalAppGraphs.SaveObservation(observationRequest)
                : await Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).CreateAppGraphObservationAsync(
                    observationRequest,
                    cancellationToken).ConfigureAwait(false);
            saveSucceeded = saveResult.IsSuccess;
            saveMessage = createdGraph
                ? $"Created draft App Graph '{graph.Name}' ({graph.Id:D}). {saveResult.Message}"
                : saveResult.Message;
            if (saveResult.IsSuccess) observationId = saveResult.ObservationId;
        }

        var result = new AppGraphExploreOutput(
            "ansight.app-graph-exploration/v2",
            graph?.Id,
            detail?.Version.Id,
            createdGraph,
            snapshot.SessionId,
            snapshot.AppId,
            agentResult.Audit.RunId,
            observationId,
            candidate.DestinationCount,
            candidate.ElementActionCount,
            candidate.Confidence,
            candidate.Summary,
            candidate.Definition,
            evidence,
            saveSucceeded,
            saveMessage);
        if (arguments.GetOption("result-file") is { } resultFile)
        {
            var resultPath = Path.GetFullPath(resultFile);
            var resultDirectory = Path.GetDirectoryName(resultPath);
            if (!string.IsNullOrWhiteSpace(resultDirectory)) Directory.CreateDirectory(resultDirectory);
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(result, jsonOptions),
                cancellationToken).ConfigureAwait(false);
        }

        output.Write(
            result,
            () => $"Exploration {agentResult.Audit.RunId}: observed {candidate.DestinationCount} destinations and {candidate.ElementActionCount} element actions.\n"
                  + candidate.Summary + Environment.NewLine + saveMessage);
        return saveSucceeded ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    internal static AppGraphExploreCloudTarget ResolveLocalExploreTarget(
        RuntimeCoordinator runtime,
        string appId,
        string appName,
        CliArguments arguments)
    {
        var registeredApp = runtime.LocalAppGraphs.ResolveApp(appId, appName);
        var graphReference = arguments.GetOption("graph")?.Trim();
        var requestedNewGraphName = arguments.GetOption("new-graph")?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedNewGraphName))
        {
            return new AppGraphExploreCloudTarget(
                registeredApp,
                Graph: null,
                Detail: null,
                requestedNewGraphName,
                ShouldCreate: true);
        }

        CloudAppGraphDetailResult detailResult;
        if (Guid.TryParse(graphReference, out var graphId) && graphId != Guid.Empty)
        {
            var belongsToApp = runtime.LocalAppGraphs.List(appId).Graphs.Any(graph => graph.Id == graphId);
            if (!belongsToApp)
            {
                throw new CliUsageException(
                    $"Local App Graph '{graphId:D}' does not belong to app '{appId}'.");
            }
            detailResult = runtime.LocalAppGraphs.Get(graphId, arguments.HasFlag("published"));
        }
        else
        {
            var graphName = string.IsNullOrWhiteSpace(graphReference) ? "Default" : graphReference;
            detailResult = runtime.LocalAppGraphs.Find(appId, graphName, arguments.HasFlag("published"));
            if (!detailResult.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(graphReference))
                {
                    throw new CliUsageException(
                        $"Local App Graph '{graphReference}' was not found for app '{appId}'. "
                        + $"Use --new-graph '{graphReference}' to create it.");
                }
                if (arguments.HasFlag("published"))
                {
                    throw new CliUsageException(
                        $"App '{appId}' does not have a published local Default graph.");
                }
                return new AppGraphExploreCloudTarget(
                    registeredApp,
                    Graph: null,
                    Detail: null,
                    graphName,
                    ShouldCreate: true);
            }
        }

        if (!detailResult.IsSuccess || detailResult.Detail is null)
        {
            throw new InvalidOperationException(detailResult.Message);
        }
        return new AppGraphExploreCloudTarget(
            registeredApp,
            detailResult.Detail.Graph,
            detailResult.Detail,
            detailResult.Detail.Graph.Name,
            ShouldCreate: false);
    }
internal static Task<AppGraphExploreCloudTarget> ResolveExploreCloudTargetAsync(
        RuntimeCoordinator runtime,
        string appId,
        string appName,
        CliArguments arguments,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<AppGraphExploreCloudTarget>>("HostedGraphCommands.ResolveExploreCloudTargetAsync", [runtime, appId, appName, arguments, cancellationToken]);
internal static Task<CloudRegisteredApp> ResolveRegisteredAppAsync(
        RuntimeCoordinator runtime,
        Guid? requestedTeamId,
        string appId,
        string appName,
        bool allowRegistration,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<CloudRegisteredApp>>("HostedGraphCommands.ResolveRegisteredAppAsync", [runtime, requestedTeamId, appId, appName, allowRegistration, cancellationToken]);
internal static Task<Guid> ResolveSingleCurrentTeamIdAsync(
        RuntimeCoordinator runtime,
        string appId,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<Guid>>("HostedGraphCommands.ResolveSingleCurrentTeamIdAsync", [runtime, appId, cancellationToken]);
internal static Task<CloudRegisteredApp> LoadRegisteredGraphAppAsync(
        RuntimeCoordinator runtime,
        CloudAppGraphSummary graph,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<CloudRegisteredApp>>("HostedGraphCommands.LoadRegisteredGraphAppAsync", [runtime, graph, cancellationToken]);

    internal static void EnsureGraphTargetsApp(string graphName, string graphAppId, string requestedAppId)
    {
        if (!graphAppId.Equals(requestedAppId, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException(
                $"App Graph '{graphName}' belongs to '{graphAppId}', not targeted app '{requestedAppId}'.");
        }
    }

    internal static string BuildDefaultGraphIntent(string appName, string? focus)
        => string.IsNullOrWhiteSpace(focus)
            ? $"Agentically discovered destinations and element actions for {appName}."
            : $"Agentically discovered destinations and element actions for {appName}, focused on {focus.Trim()}.";

    internal static Task<WorkspaceTestRunPreparation> PrepareAppGraphAgentRunAsync(
        IWorkspaceTestRunGateway? gateway,
        string appId,
        string model,
        string workspacePath,
        Guid? teamId,
        string operationId,
        string operationName,
        CancellationToken cancellationToken,
        string reasoning = AgentReasoningModes.Fast)
    {
        if (gateway is null)
        {
            return Task.FromResult(WorkspaceTestRunPreparation.Failure(
                "App Graph execution requires the Ansight cloud gateway. Sign in with 'ansight account login'."));
        }

        return gateway.PrepareAsync(
            new WorkspaceTestRunPreparationRequest(
                teamId,
                workspacePath,
                operationId,
                operationName,
                appId,
                model,
                ValidationAssertionCount: 0,
                InstructionCount: 1,
                IsDefinition: false)
            {
                Reasoning = AgentReasoningModes.Normalize(reasoning)
            },
            cancellationToken);
    }

    internal static SimulatorAgentRunRequest ConfigureAppGraphAgentRequest(
        SimulatorAgentRunRequest request,
        WorkspaceTestRunPreparation preparation,
        int requestedMaximumRoundTrips,
        string workspacePath,
        string operationId,
        string operationName)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preparation);
        var maximumRoundTrips = Math.Min(
            requestedMaximumRoundTrips,
            preparation.MaximumRoundTrips ?? requestedMaximumRoundTrips);
        var maximumWorkTurns = maximumRoundTrips > 1
            ? maximumRoundTrips - 1
            : maximumRoundTrips;
        var reasoningConfiguration = preparation.ReasoningConfiguration
                                     ?? AgentReasoningConfiguration.CreateDefault(request.Reasoning, request.Model);
        return request with
        {
            Model = reasoningConfiguration.Model,
            Reasoning = reasoningConfiguration.Reasoning,
            ReasoningEffort = reasoningConfiguration.ReasoningEffort,
            ReasoningConfigurationRevision = reasoningConfiguration.Revision,
            MaximumTurnsPerInstruction = Math.Min(
                request.MaximumTurnsPerInstruction,
                maximumWorkTurns),
            MaximumRoundTrips = maximumRoundTrips,
            ModelTransport = preparation.ModelTransport,
            StartupSteps = preparation.StartupSteps,
            TrackingRunId = preparation.TrackingRunId,
            WorkspacePath = workspacePath,
            WorkspaceTestId = operationId,
            WorkspaceTestName = operationName
        };
    }

    internal static Task<CloudAppGraphDetailResult> GetAppGraphAsync(
        RuntimeCoordinator runtime,
        Guid graphId,
        CliArguments arguments,
        CancellationToken cancellationToken)
        => AppGraphStoreOptions.Resolve(arguments) == AppGraphStoreMode.Local
            ? Task.FromResult(runtime.LocalAppGraphs.Get(graphId, arguments.HasFlag("published")))
            : Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<IAppGraphOperations>("Graph.CreateStore", [runtime]).GetAppGraphAsync(
                graphId,
                arguments.HasFlag("published"),
                cancellationToken);
internal static Task<int> SyncAsync(
        RuntimeCoordinator runtime,
        string direction,
        Guid graphId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken) => Ansight.Cli.Extensions.CliExtensionDispatch.Invoke<Task<int>>("HostedGraphCommands.SyncAsync", [runtime, direction, graphId, arguments, output, cancellationToken]);

    internal static AppGraphSyncPayload BuildSyncPayload(CloudAppGraphDetail detail)
    {
        var definition = detail.Version.Definition.DeepClone().AsObject();
        var allCandidates = new JsonArray();
        var candidatesByEdge = detail.Bindings
            .GroupBy(static binding => binding.EdgeId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.Select(binding => new JsonObject
                {
                    ["edgeId"] = binding.EdgeId,
                    ["mechanism"] = binding.Mechanism,
                    ["configuration"] = binding.Configuration.DeepClone(),
                    ["preconditions"] = JsonSerializer.SerializeToNode(binding.Preconditions),
                    ["postconditions"] = JsonSerializer.SerializeToNode(binding.Postconditions),
                    ["confidence"] = binding.Confidence
                }).ToArray(),
                StringComparer.Ordinal);
        if (definition["edges"] is JsonArray edges)
        {
            foreach (var edge in edges.OfType<JsonObject>())
            {
                var edgeId = edge["id"]?.GetValue<string>();
                if (edgeId is null || !candidatesByEdge.TryGetValue(edgeId, out var candidates))
                {
                    continue;
                }
                var edgeCandidates = new JsonArray();
                foreach (var candidate in candidates)
                {
                    edgeCandidates.Add(candidate.DeepClone());
                    allCandidates.Add(candidate.DeepClone());
                }
                edge["bindingCandidates"] = edgeCandidates;
            }
        }
        return new AppGraphSyncPayload(definition, allCandidates);
    }

    internal static async Task<AppGraphBindingExecution> ExecuteBindingAsync(
        RuntimeCoordinator runtime,
        string sessionId,
        string appId,
        Guid? teamId,
        AppGraphPlanStep step,
        CloudAppGraphBinding binding,
        JsonObject parameters,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var configuration = ExpandTemplates(binding.Configuration, parameters) as JsonObject ?? new JsonObject();
        var request = new JsonObject
        {
            ["mechanism"] = binding.Mechanism,
            ["configuration"] = configuration.DeepClone(),
            ["preconditions"] = JsonSerializer.SerializeToNode(binding.Preconditions, jsonOptions),
            ["postconditions"] = JsonSerializer.SerializeToNode(ResolvePostconditions(step.Edge, binding), jsonOptions)
        };

        if (string.Equals(binding.Mechanism, "app_tool", StringComparison.Ordinal))
        {
            var toolId = configuration["toolId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return AppGraphBindingExecution.Failure(request, "The app_tool binding does not declare configuration.toolId.");
            }

            var toolArguments = configuration["arguments"] as JsonObject ?? new JsonObject();
            var after = new JsonObject
            {
                ["include"] = new JsonArray("visualTree", "screenshot"),
                ["delayMilliseconds"] = 250
            };
            var response = await runtime.AppTools.CallWithEvidenceAsync(
                sessionId,
                toolId,
                toolArguments,
                after,
                cancellationToken).ConfigureAwait(false);
            var responseJson = JsonSerializer.SerializeToNode(response, jsonOptions) as JsonObject ?? new JsonObject();
            var isSuccess = response.Success
                            && response.Envelope is not null
                            && !string.Equals(response.Envelope.Type, "tool.error", StringComparison.Ordinal)
                            && response.Envelope.Payload?["success"]?.GetValue<bool>() != false;
            var evidence = new JsonObject
            {
                ["after"] = response.Envelope?.Payload?["after"]?.DeepClone(),
                ["protocolType"] = response.Envelope?.Type
            };
            return isSuccess
                ? AppGraphBindingExecution.Success(request, responseJson, evidence, response.Message)
                : AppGraphBindingExecution.Failure(request, response.Message, responseJson, evidence);
        }

        var postconditions = ResolvePostconditions(step.Edge, binding);
        var instruction = AppGraphTextResource.RenderSection(
            AppGraphTextResource.CatalogFileName,
            "transition-instruction",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MECHANISM"] = binding.Mechanism,
                ["CONFIGURATION"] = configuration.ToJsonString(jsonOptions),
                ["FROM_STATE"] = step.From.Name,
                ["TO_STATE"] = step.To.Name,
                ["SEMANTIC_MEANING"] = ReadSemanticMeaning(step.Edge),
                ["AUTOMATION_ID_SECTION"] = string.IsNullOrWhiteSpace(step.Edge.Action?.AutomationId)
                    ? string.Empty
                    : AppGraphTextResource.RenderSection(
                        AppGraphTextResource.CatalogFileName,
                        "transition-automation-id",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["AUTOMATION_ID"] = step.Edge.Action.AutomationId
                        }).TrimEnd() + Environment.NewLine,
                ["COMPLETION_REQUIREMENT"] = postconditions.Count == 0
                    ? AppGraphTextResource.ReadSection(
                        AppGraphTextResource.CatalogFileName,
                        "transition-completion-default")
                    : AppGraphTextResource.RenderSection(
                        AppGraphTextResource.CatalogFileName,
                        "transition-completion-postconditions",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["POSTCONDITIONS"] = string.Join("; ", postconditions)
                        }).TrimEnd()
            }).TrimEnd();
        output.WriteProgress($"[App Graph] {step.Edge.Id}: {binding.Mechanism}");
        var progress = new CliProgress<SimulatorAgentProgress>(value =>
        {
            if (arguments.IsVerbose || value.Stage is SimulatorAgentProgressStage.CallingTool or SimulatorAgentProgressStage.ToolCompleted)
            {
                output.WriteProgress($"[App Graph] [{value.Stage}] {value.Message}");
            }
        });
        var reasoning = ReasoningOptions.Resolve(arguments);
        var model = ReasoningOptions.ResolveModelOverride(arguments);
        var workspacePath = Path.Combine(runtime.BaseFolderPath, "app-graph-executions");
        var preparation = await PrepareAppGraphAgentRunAsync(
                runtime.WorkspaceTests.RunGateway,
                appId,
                model,
                workspacePath,
                teamId,
                "app-graph-run",
                "App Graph run",
                cancellationToken,
                reasoning)
            .ConfigureAwait(false);
        if (preparation.HasResolvedTeam)
        {
            output.WriteProgress(
                $"[App Graph] [organisation.selected] Hosted agent execution resolved to '{preparation.TeamName}' ({preparation.TeamId:D}).");
        }
        if (!preparation.IsSuccess)
        {
            return AppGraphBindingExecution.Failure(request, preparation.Message);
        }
        SimulatorAgentRunResult? agentResult = null;
        var completionStatus = "failed";
        var runStopwatch = Stopwatch.StartNew();
        try
        {
            var agentRequest = ConfigureAppGraphAgentRequest(
                new SimulatorAgentRunRequest(
                    sessionId,
                    [instruction],
                    model,
                    arguments.GetIntOption("max-turns", 16, 1, 128),
                    arguments.GetIntOption("max-tool-calls", 96, 1, 1_024),
                    appId,
                    ContinueAfterInstructionFailure: false)
                {
                    Reasoning = reasoning,
                    CaptureTrace = arguments.HasFlag("trace"),
                    OpenAiProtocol = ModelTransportOptions.ResolveModelTransport(arguments),
                    TargetDeviceIdentifier = arguments.GetOption("device-id") ?? arguments.GetOption("device")
                },
                preparation,
                arguments.GetIntOption("max-round-trips", 24, 1, 256),
                workspacePath,
                "app-graph-run",
                "App Graph run");
            agentResult = await runtime.SimulatorAgent.RunAsync(
                agentRequest,
                progress,
                cancellationToken).ConfigureAwait(false);
            completionStatus = agentResult.Status.ToString().ToLowerInvariant();
        }
        catch (InvalidOperationException exception)
        {
            return AppGraphBindingExecution.Failure(request, exception.Message);
        }
        catch (OperationCanceledException)
        {
            completionStatus = "cancelled";
            throw;
        }
        finally
        {
            runStopwatch.Stop();
            var meteringOutcome = await AppExecutionService.CompleteAppExecutionRunAsync(
                    runtime.WorkspaceTests.RunGateway,
                    preparation,
                    agentResult,
                    completionStatus,
                    runStopwatch.ElapsedMilliseconds,
                    runtime.SimulatorAgent.PersistRunAudit)
                .ConfigureAwait(false);
            agentResult = meteringOutcome.RunResult;
            if (meteringOutcome.Warning is not null)
            {
                output.WriteProgress($"[App Graph] [metering.warning] {meteringOutcome.Warning}");
            }
        }

        ArgumentNullException.ThrowIfNull(agentResult);
        var agentResponse = new JsonObject
        {
            ["status"] = agentResult.Status.ToString().ToLowerInvariant(),
            ["message"] = agentResult.Message,
            ["turns"] = agentResult.TotalTurns,
            ["toolCalls"] = agentResult.TotalToolCalls
        };
        var agentEvidence = new JsonObject
        {
            ["auditRunId"] = agentResult.Audit.RunId,
            ["auditFilePath"] = agentResult.AuditFilePath,
            ["successfulAnsightToolCalls"] = agentResult.Audit.SuccessfulAnsightToolCallCount,
            ["failedAnsightToolCalls"] = agentResult.Audit.FailedAnsightToolCallCount,
            ["summary"] = agentResult.Instructions.FirstOrDefault()?.Summary
        };
        return agentResult.Status == SimulatorAgentRunStatus.Succeeded
            ? AppGraphBindingExecution.Success(request, agentResponse, agentEvidence, agentResult.Message)
            : AppGraphBindingExecution.Failure(request, agentResult.Message, agentResponse, agentEvidence);
    }

    internal static IReadOnlyList<string> ResolvePostconditions(AppGraphDocumentEdge edge, CloudAppGraphBinding binding)
        => binding.Postconditions.Length > 0 ? binding.Postconditions : edge.Postconditions;

    internal static JsonNode? ExpandTemplates(JsonNode? node, JsonObject parameters)
    {
        if (node is JsonObject sourceObject)
        {
            var result = new JsonObject();
            foreach (var property in sourceObject)
            {
                result[property.Key] = ExpandTemplates(property.Value, parameters);
            }
            return result;
        }
        if (node is JsonArray sourceArray)
        {
            var result = new JsonArray();
            foreach (var item in sourceArray) result.Add(ExpandTemplates(item, parameters));
            return result;
        }
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            foreach (var parameter in parameters)
            {
                var replacement = parameter.Value is JsonValue parameterValue
                                  && parameterValue.TryGetValue<string>(out var stringValue)
                    ? stringValue
                    : parameter.Value?.ToJsonString(jsonOptions) ?? string.Empty;
                text = text.Replace($"{{{{{parameter.Key}}}}}", replacement, StringComparison.Ordinal);
            }
            return JsonValue.Create(text);
        }
        return node?.DeepClone();
    }

    internal static JsonObject ReadParameters(CliArguments arguments)
    {
        var inline = arguments.GetOption("parameters");
        var file = arguments.GetOption("parameters-file");
        if (inline is not null && file is not null)
        {
            throw new CliUsageException("Use only one of --parameters or --parameters-file.");
        }

        var source = inline;
        if (file is not null)
        {
            var path = Path.GetFullPath(file);
            if (!File.Exists(path)) throw new CliUsageException($"Parameters file '{path}' was not found.");
            source = File.ReadAllText(path);
        }
        if (string.IsNullOrWhiteSpace(source)) return new JsonObject();
        try
        {
            return JsonNode.Parse(source) as JsonObject
                   ?? throw new CliUsageException("App Graph parameters must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"App Graph parameters are not valid JSON: {exception.Message}");
        }
    }

    internal static int WriteDetailFailure(string message, CliOutput output)
    {
        output.WriteError("app_graph_not_found", message, CliExitCodes.Failure);
        return CliExitCodes.Failure;
    }

    internal static Guid ParseGraphId(string value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new CliUsageException($"'{value}' is not a valid App Graph ID.");

    internal static Guid ParseRequiredGuidOption(CliArguments arguments, string optionName, string label)
    {
        var source = arguments.RequireOption(optionName);
        return Guid.TryParse(source, out var value) && value != Guid.Empty
            ? value
            : throw new CliUsageException($"--{optionName} must be a non-empty {label} UUID.");
    }

    internal static string FormatPlan(CloudAppGraphDetail detail, AppGraphPlan plan)
    {
        if (plan.Errors.Count > 0)
        {
            return "Unable to compile the App Graph:" + Environment.NewLine
                   + string.Join(Environment.NewLine, plan.Errors.Select(error => $"- {error}"));
        }
        return $"{detail.Graph.Name} · version {detail.Version.VersionNumber}\n"
               + string.Join(
                   Environment.NewLine,
                   plan.Steps.Select((step, index) =>
                       $"{index + 1}. {step.From.Name} --{ReadSemanticMeaning(step.Edge)}--> {step.To.Name} "
                       + $"[{string.Join(" -> ", step.Bindings.Select(binding => binding.Mechanism))}]"));
    }

    internal static string ReadSemanticMeaning(AppGraphDocumentEdge edge)
        => edge.Action?.SemanticMeaning ?? string.Empty;

    internal static string BuildHelp()
        => """
           Explore, plan, and run App Graphs (BETA)

           BETA FEATURE: App Graph commands are experimental and may change before general availability.

           Usage:
             ansight app-graph list [--app-id <id>] [--search <text>]
             ansight app-graph show <graph-id> [--published]
             ansight app-graph validate <graph-id> [--published]
             ansight app-graph plan <graph-id> [--target <destination>] [--published]
             ansight app-graph run <graph-id> <live-session-id> [options]
             ansight app-graph explore [<app-id>] [--graph <name-or-id>|--new-graph <name>] [options]

           Storage options:
             --graph-store local|hosted       Graph storage; default: local, no Ansight account required
             --team-id <uuid>                 Organisation for explicitly selected hosted storage

           Cloud sync (sign-in required):
             ansight app-graph sync push <local-graph-id> --team-id <uuid>
             ansight app-graph sync pull <hosted-graph-id> --app-id <id> [--published]

           Run options:
             --parameters <JSON>              Substitute {{name}} values in binding configurations
             --parameters-file <path>         Read parameters from a JSON object file
             --target <destination>           Match a destination ID, name, synonym, or purpose
             --published                      Use the published graph version
             --headless                       Do not open windows for device starts during execution
             --reasoning <mode>              Reasoning mode: fast (default), balanced, or deep
             --model-transport <mode>         Model connection: auto (default), websocket, or http
             --device-id <identifier>         Target a specific simulator or device
             --trace                          Capture the full exportable agent trace

           Explore options:
             --app-id <id>                    Target an app explicitly; otherwise use this workspace
             --workspace <path>               Workspace used for implicit app selection; default: current folder
             --session-id <id>                Override the host-linked live session for the app
             --launch                         Launch the app when no linked session is connected
             --headless                       Do not open simulator/emulator windows; shown by default
             --no-launch                      Do not offer the interactive launch prompt
             --wait-seconds <seconds>          App-to-host connection timeout after launch; default: 45
             --platform <ios|android>          Restrict automatic launch to one platform
             --device-kind <physical|virtual> Restrict automatic launch to one device kind
             --team-id <uuid>                 Select an organisation when the app is registered more than once
             --graph <name-or-id>              Add evidence to an existing graph by name or UUID
             --new-graph <name>                Create a separate draft graph after successful exploration
             --focus <text>                   Prioritise an app area without defining a goal
             --max-actions <count>            Maximum safe navigation actions; default: 1024
             --max-turns <count>              Model work turns; default: 1023
             --max-round-trips <count>        Organisation model round trips; default: 1024
             --max-tool-calls <count>          Ansight tool calls; default: 4000
             --notes <text>                   Notes shown with the reviewable observation
             --no-save                        Return the candidate without saving an observation
             --result-file <path>             Also write the structured exploration result locally
             --reasoning <mode>              Reasoning mode: fast (default), balanced, or deep
             --model-transport <mode>         Model connection: auto (default), websocket, or http
             --device-id <identifier>         Target a specific simulator or device
             --trace                          Capture the full exportable agent trace

           With no --graph, exploration reuses the app's Default graph or creates it as a reviewable
           draft after a successful run. The resident host selects the latest linked session for the
           target app from its connection and feedback activity. If none is live, an interactive CLI
           offers to launch the installed app on a compatible target and waits for its Ansight session.

           Cloud aliases:
             ansight cloud app-graph list --team-id <uuid>
             ansight cloud app-graph show <graph-id> [--published]
           """;
}

internal sealed record AppGraphBindingExecution(
    bool IsSuccess,
    string Message,
    JsonObject Request,
    JsonObject Response,
    JsonObject Evidence)
{
    public static AppGraphBindingExecution Success(
        JsonObject request,
        JsonObject response,
        JsonObject evidence,
        string message)
        => new(true, message, request, response, evidence);

    public static AppGraphBindingExecution Failure(
        JsonObject request,
        string message,
        JsonObject? response = null,
        JsonObject? evidence = null)
        => new(false, message, request, response ?? new JsonObject(), evidence ?? new JsonObject());
}

internal sealed record AppGraphExecutedStep(
    int StepIndex,
    string EdgeId,
    Guid BindingId,
    string Mechanism,
    string Status,
    string Message);

internal sealed record AppGraphListOutput(string Schema, CloudAppGraphQueryResult Result);

internal sealed record AppGraphDetailOutput(string Schema, CloudAppGraphDetailResult Result);

internal sealed record AppGraphPlanOutput(
    string Schema,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    AppGraphPlan Plan);

internal sealed record AppGraphValidationOutput(
    string Schema,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    AppGraphValidation Validation);

internal sealed record AppGraphRunOutput(
    string Schema,
    Guid RunId,
    Guid AppGraphId,
    Guid AppGraphVersionId,
    bool IsSuccess,
    string Message,
    IReadOnlyList<AppGraphExecutedStep> Steps);

internal sealed record AppGraphSyncOutput(
    string Schema,
    string Direction,
    Guid LocalGraphId,
    Guid RemoteGraphId,
    Guid RemoteTeamId,
    Guid? RemoteVersionId,
    string Message);

internal sealed record AppGraphSyncPayload(
    JsonObject Definition,
    JsonArray BindingCandidates);

internal sealed record AppGraphExploreOutput(
    string Schema,
    Guid? AppGraphId,
    Guid? AppGraphVersionId,
    bool CreatedGraph,
    string SessionId,
    string AppId,
    string AuditRunId,
    Guid? ObservationId,
    int DestinationCount,
    int ElementActionCount,
    decimal Confidence,
    string Summary,
    JsonObject CandidateDefinition,
    JsonObject Evidence,
    bool WasSaved,
    string SaveMessage);

internal sealed record AppGraphExploreCloudTarget(
    CloudRegisteredApp RegisteredApp,
    CloudAppGraphSummary? Graph,
    CloudAppGraphDetail? Detail,
    string GraphName,
    bool ShouldCreate);

internal sealed record AppGraphExploreLiveTarget(
    string AppId,
    AppSessionSnapshot Session,
    bool UsedLegacySessionTarget);

internal static class AppGraphExploreTargetResolver
{
    public static string ResolveAppIdForLaunch(
        string requestedTarget,
        IReadOnlyList<AppSessionSnapshot> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedTarget);
        ArgumentNullException.ThrowIfNull(sessions);
        var normalizedTarget = requestedTarget.Trim();
        return sessions
                   .Where(session => session.SessionId.Equals(normalizedTarget, StringComparison.Ordinal))
                   .OrderByDescending(static session => session.LastUpdatedUtc)
                   .Select(static session => session.AppId)
                   .FirstOrDefault()
               ?? normalizedTarget;
    }

    public static AppGraphExploreLiveTarget Resolve(
        string requestedAppId,
        string? requestedSessionId,
        IReadOnlyList<AppSessionSnapshot> sessions,
        IReadOnlyList<string> connectedSessionIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedAppId);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(connectedSessionIds);
        var normalizedTarget = requestedAppId.Trim();
        var connected = connectedSessionIds.ToHashSet(StringComparer.Ordinal);
        var liveSessions = sessions
            .Where(session => connected.Contains(session.SessionId))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            var normalizedSessionId = requestedSessionId.Trim();
            var selected = liveSessions.FirstOrDefault(session =>
                session.SessionId.Equals(normalizedSessionId, StringComparison.Ordinal));
            if (selected is null)
            {
                throw new CliHostUnavailableException(
                    $"Session '{normalizedSessionId}' is not currently linked to the Ansight host.");
            }
            if (!selected.AppId.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliUsageException(
                    $"Session '{normalizedSessionId}' belongs to app '{selected.AppId}', not targeted app '{normalizedTarget}'.");
            }

            return new AppGraphExploreLiveTarget(selected.AppId, selected, UsedLegacySessionTarget: false);
        }

        var legacySession = liveSessions.FirstOrDefault(session =>
            session.SessionId.Equals(normalizedTarget, StringComparison.Ordinal));
        if (legacySession is not null)
        {
            return new AppGraphExploreLiveTarget(
                legacySession.AppId,
                legacySession,
                UsedLegacySessionTarget: true);
        }

        var selectedForApp = liveSessions
            .Where(session => session.AppId.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static session => session.LastUpdatedUtc)
            .FirstOrDefault();
        if (selectedForApp is null)
        {
            throw new CliHostUnavailableException(
                $"No live session is linked to app '{normalizedTarget}'. Run interactively to launch it, pass --launch, or open the app yourself and retry after the Ansight connection notification.");
        }

        return new AppGraphExploreLiveTarget(
            selectedForApp.AppId,
            selectedForApp,
            UsedLegacySessionTarget: false);
    }
}
