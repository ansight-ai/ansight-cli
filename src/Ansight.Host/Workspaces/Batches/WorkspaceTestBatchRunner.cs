using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Workspaces.Batches;

public sealed partial class WorkspaceTestBatchRunner
{
    private readonly Func<
        WorkspaceTestRunRequest,
        IProgress<WorkspaceTestRunProgress>?,
        CancellationToken,
        Task<WorkspaceTestRunResult>> runTest;
    private readonly WorkspaceTestBatchAuditStore batchAuditStore;
    private readonly ProductAnalytics? analytics;

    internal WorkspaceTestBatchRunner(
        Func<
            WorkspaceTestRunRequest,
            IProgress<WorkspaceTestRunProgress>?,
            CancellationToken,
            Task<WorkspaceTestRunResult>> runTest,
        WorkspaceTestBatchAuditStore batchAuditStore, ProductAnalytics? analytics = null)
    {
        this.runTest = runTest;
        this.batchAuditStore = batchAuditStore;
        this.analytics = analytics;
    }

    public Task<WorkspaceTestBatchResult> RunAllAsync(
        WorkspaceTestBatchRequest request,
        IProgress<WorkspaceTestBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => analytics is null ? RunMeasuredCoreAsync(request, progress, cancellationToken)
            : analytics.ObserveUsageAsync("test_batch",
                () => RunMeasuredCoreAsync(request, progress, cancellationToken), result =>
                {
                    analytics.RecordUsage("batch_test", outcome: "succeeded", count: result.PassedCount);
                    analytics.RecordUsage("batch_test", outcome: "failed", count: result.FailedCount);
                    analytics.RecordUsage("batch_test", outcome: "skipped", count: result.SkippedCount);
                    return result.WasCancelled ? "cancelled" : result.FailedCount > 0 ? "failed" : "succeeded";
                });

    private async Task<WorkspaceTestBatchResult> RunMeasuredCoreAsync(
        WorkspaceTestBatchRequest request,
        IProgress<WorkspaceTestBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalog = WorkspaceTestCatalog.Load(request.WorkspacePath, cancellationToken);
        var requestedIds = request.TestIds is { Count: > 0 }
            ? request.TestIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var tests = catalog.Tests
            .Where(static test => test.Enabled)
            .Where(test => requestedIds is null || requestedIds.Contains(test.TestId))
            .ToArray();
        IReadOnlyList<WorkspaceTestTargetRequest?> batchTargets = request.Targets is { Count: > 0 }
            ? request.Targets.Cast<WorkspaceTestTargetRequest?>().ToArray()
            : [request.Target];
        var totalRunCount = request.Parallel
            ? tests.Length
            : tests.Length * batchTargets.Count;
        var batchRunId = Guid.CreateVersion7().ToString("N");
        var startedUtc = DateTimeOffset.UtcNow;
        var batchAudit = new WorkspaceTestBatchRunAudit(
            1,
            batchRunId,
            string.IsNullOrWhiteSpace(request.Source) ? "host" : request.Source.Trim(),
            catalog.WorkspacePath,
            request.TestIds?.ToArray() ?? [],
            request.Model,
            request.MaximumTurnsPerInstruction,
            request.MaximumRoundTrips,
            request.MaximumToolCalls,
            request.ContinueAfterTestFailure,
            request.EnableWorkspaceTools,
            request.Target,
            request.TeamId,
            startedUtc,
            null,
            WorkspaceTestHistoryStatuses.Running,
            request.Parallel
                ? $"Running {tests.Length:N0} workspace test(s) in parallel across "
                  + $"{batchTargets.Count:N0} target(s)."
                : $"Running {tests.Length:N0} workspace test(s) across {batchTargets.Count:N0} target(s).",
            request.Parallel
                ? tests.Select((test, testIndex) => WorkspaceTestBatchItemAudit.Pending(
                    testIndex + 1,
                    test)).ToArray()
                : tests.SelectMany((test, testIndex) => batchTargets.Select((target, targetIndex) =>
                    WorkspaceTestBatchItemAudit.Pending(
                        (testIndex * batchTargets.Count) + targetIndex + 1,
                        test,
                        target))).ToArray())
        {
            RequestedTargets = request.Targets,
            Reasoning = AgentReasoningModes.Normalize(request.Reasoning),
            Parallel = request.Parallel
        };
        var historySave = batchAuditStore.Save(batchAudit);
        var results = new List<WorkspaceTestRunResult>();
        var resolvedTargets = batchTargets.ToArray();
        var auditGate = new object();
        var wasCancelled = false;
        try
        {
            if (request.Parallel)
            {
                await RunTestsInParallelAsync().ConfigureAwait(false);
            }
            else
            {
                await RunTestMatrixAsync().ConfigureAwait(false);
            }

            var result = new WorkspaceTestBatchResult(
                results,
                results.Count(static result => result.IsSuccess),
                results.Count(static result => !result.IsSuccess && !result.IsSkipped && result.Test is not null),
                results.Count(static result => result.IsSkipped) + Math.Max(0, totalRunCount - results.Count),
                wasCancelled)
            {
                BatchRunId = batchRunId,
                Parallel = request.Parallel
            };
            batchAudit = CompleteBatchAudit(batchAudit, result, DateTimeOffset.UtcNow);
            historySave = batchAuditStore.Save(batchAudit);
            return result with
            {
                HistoryFilePath = historySave.FilePath,
                HistoryPersistenceError = historySave.ErrorMessage
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            batchAudit = CompleteInterruptedBatchAudit(
                batchAudit,
                WorkspaceTestHistoryStatuses.Cancelled,
                "Workspace test batch was cancelled.");
            batchAuditStore.Save(batchAudit);
            throw;
        }
        catch (Exception exception)
        {
            batchAudit = CompleteInterruptedBatchAudit(
                batchAudit,
                WorkspaceTestHistoryStatuses.Failed,
                $"Workspace test batch failed: {exception.Message}");
            batchAuditStore.Save(batchAudit);
            throw;
        }

        async Task RunTestMatrixAsync()
        {
            for (var testIndex = 0; testIndex < tests.Length; testIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    wasCancelled = true;
                    break;
                }

                var test = tests[testIndex];
                progress?.Report(new WorkspaceTestBatchProgress(
                    testIndex + 1,
                    tests.Length,
                    test.TestId,
                    $"Starting workspace test {testIndex + 1} of {tests.Length}: {test.Name} on "
                    + $"{batchTargets.Count:N0} target(s).")
                {
                    TargetCount = batchTargets.Count
                });
                var startBarrier = batchTargets.Count > 1
                    ? new WorkspaceTestRunStartBarrier(batchTargets.Count)
                    : null;
                var targetRuns = resolvedTargets.Select((target, targetIndex) => RunTargetAsync(
                    test,
                    testIndex,
                    target,
                    targetIndex,
                    batchTargets.Count,
                    startBarrier?.CreateParticipant())).ToArray();
                var testResults = await Task.WhenAll(targetRuns).ConfigureAwait(false);
                results.AddRange(testResults);
                for (var targetIndex = 0; targetIndex < testResults.Length; targetIndex++)
                {
                    var testResult = testResults[targetIndex];
                    var requestedTarget = resolvedTargets[targetIndex];
                    resolvedTargets[targetIndex] = WorkspaceTestService.PinBatchTarget(
                        requestedTarget,
                        testResult.Target);
                    batchAudit = ReplaceBatchItem(
                        batchAudit,
                        WorkspaceTestBatchItemAudit.FromResult(
                            (testIndex * batchTargets.Count) + targetIndex + 1,
                            test,
                            testResult,
                            requestedTarget));
                }
                historySave = batchAuditStore.Save(batchAudit);
                if (testResults.Any(static result => !result.IsSuccess && !result.IsSkipped)
                    && !request.ContinueAfterTestFailure)
                {
                    break;
                }
            }
        }

        async Task RunTestsInParallelAsync()
        {
            var workerCount = Math.Min(tests.Length, batchTargets.Count);
            if (workerCount == 0)
            {
                return;
            }

            var scheduleGate = new object();
            var nextTestIndex = 0;
            var stopScheduling = false;
            var testResults = new WorkspaceTestRunResult?[tests.Length];
            var startBarrier = workerCount > 1
                ? new WorkspaceTestRunStartBarrier(workerCount)
                : null;
            var workers = Enumerable.Range(0, workerCount)
                .Select(RunWorkerAsync)
                .ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            results.AddRange(testResults.OfType<WorkspaceTestRunResult>());

            async Task RunWorkerAsync(int workerIndex)
            {
                var isFirstTest = true;
                while (TryClaimTest(out var testIndex))
                {
                    var test = tests[testIndex];
                    var requestedTarget = resolvedTargets[workerIndex];
                    progress?.Report(new WorkspaceTestBatchProgress(
                        testIndex + 1,
                        tests.Length,
                        test.TestId,
                        $"Starting workspace test {testIndex + 1} of {tests.Length}: {test.Name} on "
                        + $"target {workerIndex + 1} of {workerCount}.")
                    {
                        TargetIndex = workerIndex + 1,
                        TargetCount = workerCount,
                        DeviceIdentifier = requestedTarget?.DeviceIdentifier
                    });
                    var startParticipant = isFirstTest
                        ? startBarrier?.CreateParticipant()
                        : null;
                    isFirstTest = false;
                    var testResult = await RunTargetAsync(
                        test,
                        testIndex,
                        requestedTarget,
                        workerIndex,
                        workerCount,
                        startParticipant).ConfigureAwait(false);
                    testResults[testIndex] = testResult;
                    resolvedTargets[workerIndex] = WorkspaceTestService.PinBatchTarget(
                        requestedTarget,
                        testResult.Target);

                    lock (auditGate)
                    {
                        batchAudit = ReplaceBatchItem(
                            batchAudit,
                            WorkspaceTestBatchItemAudit.FromResult(
                                testIndex + 1,
                                test,
                                testResult,
                                requestedTarget));
                        historySave = batchAuditStore.Save(batchAudit);
                    }

                    if (!testResult.IsSuccess && !testResult.IsSkipped && !request.ContinueAfterTestFailure)
                    {
                        lock (scheduleGate)
                        {
                            stopScheduling = true;
                        }
                    }

                    if (testResult.Target is null)
                    {
                        break;
                    }
                }
            }

            bool TryClaimTest(out int testIndex)
            {
                lock (scheduleGate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        wasCancelled = true;
                        testIndex = -1;
                        return false;
                    }

                    if (stopScheduling || nextTestIndex >= tests.Length)
                    {
                        testIndex = -1;
                        return false;
                    }

                    testIndex = nextTestIndex;
                    nextTestIndex++;
                    return true;
                }
            }
        }

        async Task<WorkspaceTestRunResult> RunTargetAsync(
            WorkspaceTestDefinition test,
            int testIndex,
            WorkspaceTestTargetRequest? target,
            int targetIndex,
            int targetCount,
            WorkspaceTestRunStartParticipant? startParticipant)
        {
            var testProgress = progress is null
                ? null
                : new DelegatingProgress<WorkspaceTestRunProgress>(value => progress.Report(
                    new WorkspaceTestBatchProgress(
                        testIndex + 1,
                        tests.Length,
                        test.TestId,
                        value.Message,
                        value.AgentProgress,
                        value)
                    {
                        TargetIndex = targetIndex + 1,
                        TargetCount = targetCount,
                        DeviceIdentifier = target?.DeviceIdentifier
                    }));
            try
            {
                return await runTest(
                    new WorkspaceTestRunRequest(
                        request.WorkspacePath,
                        test.TestId,
                        SessionWaitTimeout: request.SessionWaitTimeout,
                        Model: request.Model,
                        MaximumTurnsPerInstruction: request.MaximumTurnsPerInstruction,
                        MaximumRoundTrips: request.MaximumRoundTrips,
                        MaximumToolCalls: request.MaximumToolCalls,
                        Target: target,
                        TeamId: request.TeamId,
                        EnableWorkspaceTools: request.EnableWorkspaceTools)
                    {
                        BatchRunId = batchRunId,
                        OperationContext = new OperationExecutionContext(batchRunId, test.TestId,
                            request.Parallel || batchTargets.Count > 1,
                            message => testProgress?.Report(new WorkspaceTestRunProgress("audio-rejected", message))),
                        Reasoning = request.Reasoning,
                        CaptureTrace = request.CaptureTrace,
                        OpenAiProtocol = request.OpenAiProtocol,
                        SecretResolver = request.SecretResolver,
                        StartParticipant = startParticipant
                    },
                    testProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                startParticipant?.Dispose();
            }
        }
    }

}

public sealed partial class WorkspaceTestBatchRunner
{
    private static WorkspaceTestBatchRunAudit ReplaceBatchItem(
        WorkspaceTestBatchRunAudit audit,
        WorkspaceTestBatchItemAudit item)
    {
        var tests = audit.Tests.ToArray();
        var itemIndex = item.Index - 1;
        if (itemIndex < 0 || itemIndex >= tests.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(item), "Batch item index is outside the test collection.");
        }

        tests[itemIndex] = item;
        return audit with { Tests = tests };
    }

    private static WorkspaceTestBatchRunAudit CompleteBatchAudit(
        WorkspaceTestBatchRunAudit audit,
        WorkspaceTestBatchResult result,
        DateTimeOffset completedUtc)
    {
        var status = result.WasCancelled
            ? WorkspaceTestHistoryStatuses.Cancelled
            : result.FailedCount > 0 || result.Results.Count == 0
                ? WorkspaceTestHistoryStatuses.Failed
                : result.PassedCount == 0 && result.SkippedCount > 0
                    ? WorkspaceTestHistoryStatuses.Skipped
                    : WorkspaceTestHistoryStatuses.Succeeded;
        var message = $"Workspace tests complete: {result.PassedCount:N0} passed, "
                      + $"{result.FailedCount:N0} failed, {result.SkippedCount:N0} skipped"
                      + (result.WasCancelled ? ", cancelled." : ".");
        return audit with
        {
            CompletedUtc = completedUtc,
            Status = status,
            Message = message,
            Tests = MarkPendingItemsSkipped(
                audit.Tests,
                result.WasCancelled
                    ? "Not run because the batch was cancelled."
                    : "Not run because batch execution stopped early.")
        };
    }

    private static WorkspaceTestBatchRunAudit CompleteInterruptedBatchAudit(
        WorkspaceTestBatchRunAudit audit,
        string status,
        string message)
        => audit with
        {
            CompletedUtc = DateTimeOffset.UtcNow,
            Status = status,
            Message = message,
            Tests = MarkPendingItemsSkipped(audit.Tests, message)
        };

    private static IReadOnlyList<WorkspaceTestBatchItemAudit> MarkPendingItemsSkipped(
        IReadOnlyList<WorkspaceTestBatchItemAudit> tests,
        string message)
        => tests.Select(test => test.Status == WorkspaceTestHistoryStatuses.Pending
                ? test with
                {
                    Status = WorkspaceTestHistoryStatuses.Skipped,
                    Message = message
                }
                : test)
            .ToArray();

}
