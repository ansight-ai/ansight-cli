
namespace Ansight.Host.Explorer.Tests;

internal sealed class LocalTestExecutionCoordinator : IDisposable
{
    private const int MaximumRetainedRuns = 100;
    private readonly Lock gate = new();
    private readonly RuntimeCoordinator runtime;
    private readonly Dictionary<string, ActiveTestExecution> executions = new(StringComparer.Ordinal);
    private bool disposed;

    public LocalTestExecutionCoordinator(RuntimeCoordinator runtime)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IReadOnlyList<LocalTestExecutionSnapshot> List()
    {
        lock (gate)
        {
            return executions.Values
                .Select(static execution => execution.Snapshot())
                .OrderByDescending(static execution => execution.CreatedAtUtc)
                .ToArray();
        }
    }

    public LocalTestExecutionSnapshot? Get(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        lock (gate)
        {
            return executions.GetValueOrDefault(executionId.Trim())?.Snapshot();
        }
    }

    public LocalTestExecutionSnapshot Start(LocalWorkspaceTestExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        runtime.FeatureLifetime.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new InvalidDataException("A workspace path is required.");
        }

        var testIds = request.TestIds?
            .Where(static testId => !string.IsNullOrWhiteSpace(testId))
            .Select(static testId => testId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (!string.IsNullOrWhiteSpace(request.DraftSource))
        {
            var draft = Ansight.Host.Workspaces.Catalog.WorkspaceTestCatalog.Parse(
                "ansight/tests", "ansight/tests/draft.yaml", request.DraftSource);
            testIds = [draft.TestId];
        }
        var kind = testIds.Length == 1 ? "single" : "batch";
        var execution = new ActiveTestExecution(
            Guid.CreateVersion7().ToString("N"),
            kind,
            Path.GetFullPath(request.WorkspacePath.Trim()),
            testIds,
            runtime.FeatureLifetime);
        lock (gate)
        {
            executions[execution.ExecutionId] = execution;
            PruneCompletedRuns();
        }

        execution.WorkTask = kind == "single"
            ? RunSingleAsync(execution, request, testIds[0])
            : RunBatchAsync(execution, request, testIds);
        return execution.Snapshot();
    }

    public LocalTestExecutionSnapshot? Cancel(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ActiveTestExecution? execution;
        lock (gate)
        {
            execution = executions.GetValueOrDefault(executionId.Trim());
        }

        execution?.Cancel();
        return execution?.Snapshot();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ActiveTestExecution[] active;
        lock (gate)
        {
            active = executions.Values.ToArray();
        }

        foreach (var execution in active)
        {
            execution.Cancel();
        }
    }

    private async Task RunSingleAsync(
        ActiveTestExecution execution,
        LocalWorkspaceTestExecutionRequest request,
        string testId)
    {
        execution.Begin($"Running workspace test '{testId}'.");
        try
        {
            var result = await runtime.WorkspaceTests.RunAsync(
                    CreateSingleRequest(request, execution.WorkspacePath, testId),
                    new DelegatingProgress<WorkspaceTestRunProgress>(progress => execution.Report(
                        progress.Stage,
                        progress.Message)),
                    execution.Cancellation.Token)
                .ConfigureAwait(false);
            execution.Complete(
                result.IsSuccess ? "succeeded" : "failed",
                result.Message,
                new LocalTestExecutionResult(
                    result.IsSuccess,
                    result.Message,
                    result.Test?.TestId ?? testId,
                    result.SessionId,
                    result.IsSuccess ? 1 : 0,
                    result.IsSuccess ? 0 : 1,
                    0,
                    false)
                {
                    TraceRunId = result.AgentResult?.Audit.TraceEnabled == true
                        && result.AgentResult.AuditFilePath is not null
                            ? result.AgentResult.Audit.RunId : null,
                    TraceError = request.CaptureTrace
                        ? result.AgentResult?.AuditPersistenceError
                          ?? (result.AgentResult is null ? "The run stopped before the agent trace began." : null)
                        : null
                });
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested)
        {
            execution.Complete(
                "cancelled",
                "Workspace test cancelled.",
                new LocalTestExecutionResult(false, "Workspace test cancelled.", testId, null, 0, 0, 0, true));
        }
        catch (Exception exception)
        {
            execution.Complete(
                "failed",
                exception.GetBaseException().Message,
                new LocalTestExecutionResult(
                    false,
                    exception.GetBaseException().Message,
                    testId,
                    null,
                    0,
                    1,
                    0,
                    false));
        }
        finally
        {
            DeleteDraftTaskRoot(request.DraftTaskRootPath);
        }
    }

    private async Task RunBatchAsync(
        ActiveTestExecution execution,
        LocalWorkspaceTestExecutionRequest request,
        IReadOnlyList<string> testIds)
    {
        execution.Begin(testIds.Count == 0
            ? "Running all workspace tests."
            : $"Running {testIds.Count:N0} selected workspace test(s).");
        try
        {
            var result = await runtime.WorkspaceTests.Batches.RunAllAsync(
                    CreateBatchRequest(request, execution.WorkspacePath, testIds),
                    new DelegatingProgress<WorkspaceTestBatchProgress>(progress => execution.Report(
                        progress.TestProgress?.Stage ?? "test.batch",
                        progress.Message,
                        progress.TestIndex,
                        progress.TestCount,
                        progress.TestId)),
                    execution.Cancellation.Token)
                .ConfigureAwait(false);
            var status = result.WasCancelled
                ? "cancelled"
                : result.FailedCount > 0
                    ? "failed"
                    : "succeeded";
            var message = result.WasCancelled
                ? "Workspace test batch cancelled."
                : $"Completed {result.Results.Count:N0} test(s): {result.PassedCount:N0} passed, {result.FailedCount:N0} failed, {result.SkippedCount:N0} skipped.";
            execution.Complete(
                status,
                message,
                new LocalTestExecutionResult(
                    !result.WasCancelled && result.FailedCount == 0,
                    message,
                    null,
                    null,
                    result.PassedCount,
                    result.FailedCount,
                    result.SkippedCount,
                    result.WasCancelled));
        }
        catch (OperationCanceledException) when (execution.Cancellation.IsCancellationRequested)
        {
            execution.Complete(
                "cancelled",
                "Workspace test batch cancelled.",
                new LocalTestExecutionResult(false, "Workspace test batch cancelled.", null, null, 0, 0, 0, true));
        }
        catch (Exception exception)
        {
            execution.Complete(
                "failed",
                exception.GetBaseException().Message,
                new LocalTestExecutionResult(
                    false,
                    exception.GetBaseException().Message,
                    null,
                    null,
                    0,
                    1,
                    0,
                    false));
        }
    }

    private static WorkspaceTestRunRequest CreateSingleRequest(
        LocalWorkspaceTestExecutionRequest request,
        string workspacePath,
        string testId)
        => new(
            workspacePath,
            testId,
            request.SessionId,
            Model: request.Model,
            MaximumTurnsPerInstruction: request.MaximumTurnsPerInstruction,
            MaximumRoundTrips: request.MaximumRoundTrips,
            MaximumToolCalls: request.MaximumToolCalls,
            ContinueAfterInstructionFailure: request.ContinueAfterFailure,
            Target: CreateTarget(request),
            TeamId: request.TeamId,
            EnableWorkspaceTools: request.EnableWorkspaceTools)
        {
            CaptureTrace = request.CaptureTrace,
            Reasoning = AgentReasoningModes.Normalize(request.Reasoning),
            DraftSource = request.DraftSource,
            DraftTaskRootPath = request.DraftTaskRootPath
        };

    private static void DeleteDraftTaskRoot(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        var fullPath = Path.GetFullPath(rootPath);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(parent + Path.DirectorySeparatorChar + "ansight-draft-test-", StringComparison.Ordinal)) return;
        try { Directory.Delete(fullPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static WorkspaceTestBatchRequest CreateBatchRequest(
        LocalWorkspaceTestExecutionRequest request,
        string workspacePath,
        IReadOnlyList<string> testIds)
        => new(
            workspacePath,
            testIds.Count == 0 ? null : testIds,
            Model: request.Model,
            MaximumTurnsPerInstruction: request.MaximumTurnsPerInstruction,
            MaximumRoundTrips: request.MaximumRoundTrips,
            MaximumToolCalls: request.MaximumToolCalls,
            ContinueAfterTestFailure: request.ContinueAfterFailure,
            Target: CreateTarget(request),
            TeamId: request.TeamId,
            EnableWorkspaceTools: request.EnableWorkspaceTools)
        {
            CaptureTrace = request.CaptureTrace,
            Source = "local-web",
            Reasoning = AgentReasoningModes.Normalize(request.Reasoning)
        };

    private static WorkspaceTestTargetRequest? CreateTarget(LocalWorkspaceTestExecutionRequest request)
        => string.IsNullOrWhiteSpace(request.Platform)
           && string.IsNullOrWhiteSpace(request.DeviceIdentifier)
           && string.IsNullOrWhiteSpace(request.ApplicationPath)
           && string.IsNullOrWhiteSpace(request.DeviceKind)
            ? null
            : new WorkspaceTestTargetRequest(
                request.Platform,
                request.DeviceIdentifier,
                request.ApplicationPath,
                request.DeviceKind);

    private void PruneCompletedRuns()
    {
        if (executions.Count <= MaximumRetainedRuns)
        {
            return;
        }

        foreach (var executionId in executions.Values
                     .Where(static execution => execution.IsTerminal)
                     .OrderBy(static execution => execution.UpdatedAtUtc)
                     .Take(executions.Count - MaximumRetainedRuns)
                     .Select(static execution => execution.ExecutionId)
                     .ToArray())
        {
            if (executions.Remove(executionId, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private sealed class ActiveTestExecution : IDisposable
    {
        private const int MaximumProgressEntries = 250;
        private readonly Lock gate = new();
        private readonly List<LocalTestExecutionProgress> progress = [];
        private string status = "queued";
        private string message = "Workspace test execution queued.";
        private LocalTestExecutionResult? result;

        public ActiveTestExecution(
            string executionId,
            string kind,
            string workspacePath,
            IReadOnlyList<string> testIds,
            CancellationToken featureLifetime)
        {
            ExecutionId = executionId;
            Kind = kind;
            WorkspacePath = workspacePath;
            TestIds = testIds;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(featureLifetime);
            CreatedAtUtc = DateTimeOffset.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
        }

        public string ExecutionId { get; }
        public string Kind { get; }
        public string WorkspacePath { get; }
        public IReadOnlyList<string> TestIds { get; }
        public DateTimeOffset CreatedAtUtc { get; }
        public DateTimeOffset UpdatedAtUtc { get; private set; }
        public CancellationTokenSource Cancellation { get; }
        public Task? WorkTask { get; set; }

        public bool IsTerminal
        {
            get
            {
                lock (gate)
                {
                    return status is "succeeded" or "failed" or "cancelled";
                }
            }
        }

        public void Begin(string nextMessage)
        {
            lock (gate)
            {
                status = "running";
                message = nextMessage;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
                AddProgress("test.start", nextMessage, null, null, null);
            }
        }

        public void Report(
            string stage,
            string nextMessage,
            int? testIndex = null,
            int? testCount = null,
            string? testId = null)
        {
            lock (gate)
            {
                message = nextMessage;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
                AddProgress(stage, nextMessage, testIndex, testCount, testId);
            }
        }

        public void Complete(
            string nextStatus,
            string nextMessage,
            LocalTestExecutionResult nextResult)
        {
            lock (gate)
            {
                status = nextStatus;
                message = nextMessage;
                result = nextResult;
                UpdatedAtUtc = DateTimeOffset.UtcNow;
                AddProgress("test.complete", nextMessage, null, null, nextResult.TestId);
            }
        }

        public void Cancel()
        {
            lock (gate)
            {
                if (status is "succeeded" or "failed" or "cancelled")
                {
                    return;
                }

                message = "Cancelling workspace test execution.";
                UpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            Cancellation.Cancel();
        }

        public LocalTestExecutionSnapshot Snapshot()
        {
            lock (gate)
            {
                return new LocalTestExecutionSnapshot(
                    "ansight.local-test-execution/v1",
                    ExecutionId,
                    Kind,
                    status,
                    message,
                    WorkspacePath,
                    TestIds,
                    CreatedAtUtc,
                    UpdatedAtUtc,
                    progress.ToArray(),
                    result);
            }
        }

        public void Dispose()
        {
            Cancellation.Dispose();
        }

        private void AddProgress(
            string stage,
            string nextMessage,
            int? testIndex,
            int? testCount,
            string? testId)
        {
            progress.Add(new LocalTestExecutionProgress(
                stage,
                nextMessage,
                DateTimeOffset.UtcNow,
                testIndex,
                testCount,
                testId));
            if (progress.Count > MaximumProgressEntries)
            {
                progress.RemoveRange(0, progress.Count - MaximumProgressEntries);
            }
        }
    }
}
