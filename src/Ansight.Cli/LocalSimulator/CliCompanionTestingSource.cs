using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Cli.Commands.App;
using Ansight.Host;
using Ansight.Host.Workspaces;
using Ansight.RemoteSimulator.Core.Devices;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliCompanionTestingSource : IRemoteDeviceTestingSource, IAsyncDisposable
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeCoordinator runtime;
    private readonly CliRemoteAnnotationSource annotations;
    private readonly Lock gate = new();
    private readonly string historyPath;
    private readonly List<CompanionTaskRun> history = [];
    private CancellationTokenSource? activeCancellation;
    private Task? activeWorker;
    private string? activeRunId;
    private bool disposed;

    public CliCompanionTestingSource(RuntimeCoordinator runtime, CliRemoteAnnotationSource annotations)
    {
        this.runtime = runtime;
        this.annotations = annotations;
        historyPath = Path.Combine(runtime.BaseFolderPath, "companion-task-runs.json");
        if (File.Exists(historyPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<CompanionTaskRun[]>(File.ReadAllText(historyPath), jsonOptions) ?? [];
                history.AddRange(saved.Take(50).Select(run => run.IsRunning
                    ? run with { Status = "interrupted", Message = "The Mac restarted before this run completed." }
                    : run));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A damaged recent-history file must not prevent starting the local host.
            }
        }
    }

    public async Task<RemoteDeviceTestingResult> ExecuteAsync(RemoteDeviceTestingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Action is not ("task-start" or "task-status" or "task-cancel"))
        {
            var result = await runtime.CompanionDeviceTesting.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Action == "app-capabilities")
            {
                var details = result.Details ?? new JsonObject();
                details["supportsTasks"] = true;
                return result with { Details = details };
            }
            return result;
        }

        if (request.Action == "task-start")
        {
            return Start(request);
        }

        lock (gate)
        {
            var run = Find(request);
            if (run is null)
            {
                return new RemoteDeviceTestingResult(request.RunId is null, "No recent run for this device.");
            }
            var canCancel = request.Action == "task-cancel"
                && run.Id == activeRunId
                && run.IsRunning;
            if (canCancel)
            {
                activeCancellation?.Cancel();
                Update(run with { Status = "cancelling", Message = "Stopping…" });
                run = Find(request)!;
            }
            return Result(run);
        }
    }

    internal static string[] BuildInstructions(string prompt, string executionType)
    {
        if (executionType is not ("test" or "app-execute"))
        {
            throw new ArgumentException("Choose Test or App Execute.");
        }
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12_000)
        {
            throw new ArgumentException("Describe the task in 1–12,000 characters.");
        }

        var goal = prompt.Trim();
        return executionType == "test"
            ? [goal, "Validate the preceding test goal using fresh app evidence. Inspect the final UI and relevant "
                + "state or logs available for this capture. Explicitly report each expected outcome as passed, failed, "
                + "or unverifiable, with the observed evidence. Do not mark the instruction successful if an expected "
                + "outcome failed or cannot be verified. Test goal: " + goal]
            : [goal];
    }

    private RemoteDeviceTestingResult Start(RemoteDeviceTestingRequest request)
    {
        try
        {
            _ = BuildInstructions(request.Prompt ?? "", request.ExecutionType ?? "");
            if (!Guid.TryParse(request.RequestId, out _) || string.IsNullOrWhiteSpace(request.AppId))
            {
                return new RemoteDeviceTestingResult(false, "Choose an app and provide a unique run request.");
            }
            lock (gate)
            {
                if (disposed)
                {
                    return new RemoteDeviceTestingResult(false, "The host is stopping.");
                }
                var previous = history.FirstOrDefault(run => run.RequestId == request.RequestId);
                if (previous is not null)
                {
                    var matchesRequest = previous.DeviceId == request.DeviceUdid
                        && previous.AppId == request.AppId
                        && previous.Prompt == request.Prompt!.Trim()
                        && previous.ExecutionType == request.ExecutionType;
                    return matchesRequest ? Result(previous)
                        : new RemoteDeviceTestingResult(false, "This request ID belongs to a different task.");
                }
                if (activeWorker is not null && !activeWorker.IsCompleted)
                {
                    return new RemoteDeviceTestingResult(false, "A companion task is already running. Stop it or wait for it to finish.");
                }

                var run = new CompanionTaskRun(Guid.NewGuid().ToString("N"), request.RequestId!, request.DeviceUdid,
                    request.AppId!, request.ExecutionType!, request.Prompt!.Trim(), "starting", "Submitted to Mac",
                    DateTimeOffset.UtcNow, null, null, null, []);
                history.Insert(0, run);
                if (history.Count > 50)
                {
                    history.RemoveRange(50, history.Count - 50);
                }
                Persist();
                activeCancellation?.Dispose();
                activeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                activeRunId = run.Id;
                var token = activeCancellation.Token;
                activeWorker = Task.Run(() => RunAsync(run, token), CancellationToken.None);
                return Result(run);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new RemoteDeviceTestingResult(false, exception.Message);
        }
    }

    private async Task RunAsync(CompanionTaskRun started, CancellationToken token)
    {
        var run = started;
        WorkspaceTestRunPreparation? preparation = null;
        SimulatorAgentRunResult? outcome = null;
        var status = "failed";
        var timer = Stopwatch.StartNew();
        var gateway = runtime.WorkspaceTests.RunGateway;
        try
        {
            if (gateway is null)
            {
                throw new InvalidOperationException("Sign in on the Mac to run AI tasks.");
            }
            var instructions = BuildInstructions(run.Prompt, run.ExecutionType);
            var testId = run.ExecutionType == "test" ? "companion-test" : "app-execute-companion";
            var testName = run.Prompt.Split('\n')[0];
            testName = testName.Length > 100 ? testName[..100] : testName;
            var workspacePath = Path.Combine(runtime.BaseFolderPath, "companion-executions");
            preparation = await gateway.PrepareAsync(new WorkspaceTestRunPreparationRequest(
                null, workspacePath, testId, testName, run.AppId, "gpt-6-luna",
                run.ExecutionType == "test" ? 1 : 0, instructions.Length, IsDefinition: false), token).ConfigureAwait(false);
            if (!preparation.IsSuccess)
            {
                throw new InvalidOperationException(preparation.Message);
            }
            run = run with { TrackingRunId = preparation.TrackingRunId?.ToString() };
            Set(run);

            await using var launch = await SelectTargetAsync(run, token).ConfigureAwait(false);
            if (!launch.IsSuccess || launch.Session is null)
            {
                throw new InvalidOperationException(launch.Message);
            }
            run = run with { SessionId = launch.Session.SessionId, Status = "running", Message = "Running…" };
            Set(run);
            var reasoning = preparation.ReasoningConfiguration
                ?? AgentReasoningConfiguration.CreateDefault(AgentReasoningModes.Fast, "gpt-6-luna");
            var maximumRoundTrips = Math.Min(64, preparation.MaximumRoundTrips ?? 64);
            var agentRequest = new SimulatorAgentRunRequest(launch.Session.SessionId, instructions,
                reasoning.Model, maximumRoundTrips, 256, run.AppId, ContinueAfterInstructionFailure: false)
            {
                TrackingRunId = preparation.TrackingRunId,
                ModelTransport = preparation.ModelTransport,
                MaximumRoundTrips = maximumRoundTrips,
                TargetDeviceIdentifier = run.DeviceId,
                WorkspacePath = workspacePath,
                WorkspaceTestId = testId,
                WorkspaceTestName = testName,
                Reasoning = reasoning.Reasoning,
                ReasoningEffort = reasoning.ReasoningEffort,
                ReasoningConfigurationRevision = reasoning.Revision,
                StartupSteps = preparation.StartupSteps.Concat(launch.StartupSteps).ToArray()
            };
            var progress = new TaskProgress(value => ReportProgress(run.Id, value));
            outcome = await runtime.SimulatorAgent.RunAsync(agentRequest, progress, token).ConfigureAwait(false);
            status = outcome.Status.ToString().ToLowerInvariant();
            run = run with
            {
                Status = status,
                Message = outcome.Status == SimulatorAgentRunStatus.Succeeded
                    ? run.ExecutionType == "test" ? "Test passed." : "Task completed."
                    : outcome.Message,
                Results = outcome.Instructions.Select(instruction => new CompanionTaskOutcome(
                    instruction.Index, instruction.Status.ToString().ToLowerInvariant(), instruction.Summary)).ToArray()
            };
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            run = run with { Status = status, Message = "Run stopped or reached its ten-minute limit." };
        }
        catch (Exception exception)
        {
            run = run with { Status = "failed", Message = exception.Message };
        }
        finally
        {
            if (preparation is not null)
            {
                var completion = await AppExecutionService.CompleteAppExecutionRunAsync(gateway, preparation, outcome,
                    status, timer.ElapsedMilliseconds, runtime.SimulatorAgent.PersistRunAudit).ConfigureAwait(false);
                if (completion.Warning is not null)
                {
                    run = run with { Message = run.Message + " Run history will sync when available." };
                }
            }
            Set(run with { CompletedAt = DateTimeOffset.UtcNow });
        }
    }

    private async Task<WorkspaceAppSessionLaunchResult> SelectTargetAsync(CompanionTaskRun run, CancellationToken token)
    {
        var sessions = await annotations.ListLiveSessionsAsync(token).ConfigureAwait(false);
        var session = sessions.FirstOrDefault(candidate =>
            candidate.DeviceUdid.Equals(run.DeviceId, StringComparison.OrdinalIgnoreCase) && candidate.AppId == run.AppId);
        if (session is not null && runtime.IsSessionLive(session.SessionId))
        {
            if (runtime.Sessions.TryGetSnapshot(session.SessionId, out var snapshot) && snapshot is not null)
            {
                return new WorkspaceAppSessionLaunchResult(true, "Using the running app.", snapshot)
                {
                    SessionClaim = runtime.WorkspaceTests.ReserveSession(snapshot)
                };
            }
        }

        var target = new WorkspaceTestTargetRequest(DeviceIdentifier: run.DeviceId, Headless: true)
        {
            ExecutionMode = WorkspaceExecutionModes.Device
        };
        return await runtime.WorkspaceTests.LaunchAppAndWaitForReservedSessionAsync(
            run.AppId, target, null, TimeSpan.FromSeconds(45), null, token).ConfigureAwait(false);
    }

    private CompanionTaskRun? Find(RemoteDeviceTestingRequest request)
    {
        return history.FirstOrDefault(run => run.DeviceId == request.DeviceUdid
            && (request.RunId is null || run.Id == request.RunId));
    }

    private void ReportProgress(string runId, SimulatorAgentProgress progress)
    {
        lock (gate)
        {
            var current = history.FirstOrDefault(item => item.Id == runId);
            if (current is null || current.Status == "cancelling")
            {
                return;
            }

            var validating = current.ExecutionType == "test" && progress.InstructionIndex > 1;
            var message = ProgressMessage(progress, validating);
            if (message is null)
            {
                return;
            }

            var activities = current.Activities.ToList();
            if (activities.LastOrDefault()?.Message != message)
            {
                activities.Add(new CompanionTaskActivity(DateTimeOffset.UtcNow, message, progress.InstructionIndex));
            }
            Update(current with
            {
                Status = validating ? "validating" : "running",
                Message = message,
                Activities = activities.TakeLast(20).ToArray()
            });
        }
    }

    internal static string? ProgressMessage(SimulatorAgentProgress progress, bool validating)
    {
        if (progress.Stage == SimulatorAgentProgressStage.Thinking)
        {
            return validating ? "Checking results" : "Planning next step";
        }
        if (progress.Stage == SimulatorAgentProgressStage.InstructionCompleted)
        {
            return progress.Message.Length <= 1200 ? progress.Message : progress.Message[..1200];
        }
        if (progress.Stage != SimulatorAgentProgressStage.CallingTool)
        {
            return null;
        }

        var tool = progress.ToolName?.ToLowerInvariant() ?? "";
        if (tool.Contains("screenshot"))
        {
            return "Taking screenshot";
        }
        if (tool.Contains("tap") || tool.Contains("click"))
        {
            return "Tapping control";
        }
        if (tool.Contains("text") || tool.Contains("type"))
        {
            return "Entering text";
        }
        if (tool.Contains("scroll") || tool.Contains("swipe"))
        {
            return "Scrolling";
        }
        if (tool.Contains("wait"))
        {
            return "Waiting for app";
        }
        if (tool.Contains("network"))
        {
            return "Checking network";
        }
        if (tool.Contains("log"))
        {
            return "Reading logs";
        }
        if (tool.Contains("ui") || tool.Contains("tree"))
        {
            return "Reading screen";
        }
        return "Running action";
    }

    private void Set(CompanionTaskRun run)
    {
        lock (gate)
        {
            Update(run);
        }
    }

    private void Update(CompanionTaskRun run)
    {
        var index = history.FindIndex(item => item.Id == run.Id);
        if (index < 0)
        {
            return;
        }
        history[index] = run with
        {
            Activities = run.Activities.Count > 0 ? run.Activities : history[index].Activities
        };
        try
        {
            Persist();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            history[index] = history[index] with { Message = run.Message + " Recent history could not be saved." };
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        var temporary = historyPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(history, jsonOptions));
        File.Move(temporary, historyPath, overwrite: true);
    }

    private static RemoteDeviceTestingResult Result(CompanionTaskRun run)
    {
        return new RemoteDeviceTestingResult(true, run.Message,
            new JsonObject { ["task"] = JsonSerializer.SerializeToNode(run, jsonOptions) });
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (gate)
        {
            disposed = true;
            activeCancellation?.Cancel();
            worker = activeWorker;
        }
        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }
        activeCancellation?.Dispose();
    }

    private sealed class TaskProgress : IProgress<SimulatorAgentProgress>
    {
        private readonly Action<SimulatorAgentProgress> report;
        public TaskProgress(Action<SimulatorAgentProgress> report)
        {
            this.report = report;
        }
        public void Report(SimulatorAgentProgress value)
        {
            report(value);
        }
    }
}

internal sealed record CompanionTaskRun(string Id, string RequestId, string DeviceId, string AppId,
    string ExecutionType, string Prompt, string Status, string Message, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, string? TrackingRunId, string? SessionId, CompanionTaskOutcome[] Results)
{
    public bool IsRunning => Status is "starting" or "running" or "validating" or "cancelling";
    public IReadOnlyList<CompanionTaskActivity> Activities { get; init; } = [];
}

internal sealed record CompanionTaskOutcome(int Index, string Status, string Summary);
internal sealed record CompanionTaskActivity(DateTimeOffset Time, string Message, int InstructionIndex);
