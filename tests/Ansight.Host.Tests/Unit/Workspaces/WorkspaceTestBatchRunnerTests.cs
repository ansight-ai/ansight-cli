using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Audio;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceTestBatchRunnerTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    [InlineData(false, 2, true)]
    public async Task RunAllAsync_RestrictsAudioInParallelModeAndConcurrentMatrix(bool parallel, int targetCount, bool shouldReject)
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath, "audio");
        var test = Assert.Single(WorkspaceTestCatalog.Load(workspacePath).Tests);
        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        var runner = new WorkspaceTestBatchRunner(async (request, _, _) =>
        {
            await Task.Yield();
            var rejection = AudioExecutionPolicy.RejectParallelCall("ansight_inject_audio", request.OperationContext);
            Assert.Equal(shouldReject, rejection is not null);
            Assert.Null(AudioExecutionPolicy.RejectParallelCall("ansight_get_screenshot", request.OperationContext));
            return new WorkspaceTestRunResult(true, "Passed.", test, "session", null, []);
        }, new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        await runner.RunAllAsync(new WorkspaceTestBatchRequest(workspacePath)
        {
            Parallel = parallel,
            Targets = Enumerable.Range(0, targetCount)
                .Select(index => new WorkspaceTestTargetRequest(DeviceIdentifier: $"device-{index}")).ToArray()
        }, new DelegatingProgress<WorkspaceTestBatchProgress>(value => messages.Add(value.Message)));

        Assert.Equal(shouldReject ? (parallel ? 1 : targetCount) : 0,
            messages.Count(message => message.Contains(AudioExecutionPolicy.ParallelErrorCode, StringComparison.Ordinal)));
        Assert.Null(AudioExecutionPolicy.RejectParallelCall("ansight_inject_audio", null));
    }

    [Fact]
    public async Task RunAllAsync_OmitsDisabledTests()
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath, "enabled", "disabled");
        var disabledPath = Path.Combine(workspacePath, "ansight", "tests", "disabled.json");
        File.WriteAllText(
            disabledPath,
            File.ReadAllText(disabledPath).Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n                  \"enabled\": false,",
                StringComparison.Ordinal));
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var executedTestIds = new List<string>();
        var runner = new WorkspaceTestBatchRunner(
            RunTestAsync,
            new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        var result = await runner.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            EnableWorkspaceTools: false));

        Assert.Equal(["enabled"], executedTestIds);
        Assert.Equal(1, result.PassedCount);
        Assert.Single(result.Results);

        Task<WorkspaceTestRunResult> RunTestAsync(
            WorkspaceTestRunRequest request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            executedTestIds.Add(request.TestId);
            Assert.Equal("fast", request.Reasoning);
            Assert.Equal(string.Empty, request.Model);
            var test = Assert.Single(catalog.Tests, candidate => candidate.TestId == request.TestId);
            return Task.FromResult(new WorkspaceTestRunResult(
                true,
                "Passed.",
                test,
                "session-1",
                null,
                []));
        }
    }

    [Fact]
    public async Task RunAllAsync_ParallelRunsEachTestOnceAcrossTargetWorkers()
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath, "first", "second", "third", "fourth");
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var stateGate = new object();
        var assignments = new List<string>();
        var activeRuns = 0;
        var maximumActiveRuns = 0;
        var targets = new[]
        {
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-one"),
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-two")
        };
        var runner = new WorkspaceTestBatchRunner(
            RunTestAsync,
            new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        var result = await runner.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            EnableWorkspaceTools: false)
        {
            Targets = targets,
            Parallel = true,
            Reasoning = AgentReasoningModes.Deep
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Parallel);
        Assert.Equal(4, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(4, result.Results.Count);
        Assert.Equal(2, maximumActiveRuns);
        Assert.Equal(
            ["first", "fourth", "second", "third"],
            result.Results.Select(run => run.Test?.TestId).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["device-one", "device-two"],
            result.Results.Select(run => run.Target?.DeviceIdentifier).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(4, assignments.Count);

        async Task<WorkspaceTestRunResult> RunTestAsync(
            WorkspaceTestRunRequest request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var startParticipant = request.StartParticipant;
            Assert.Equal("deep", request.Reasoning);
            Assert.Equal(string.Empty, request.Model);
            if (startParticipant is not null)
            {
                await startParticipant.ArriveAndWaitAsync(cancellationToken);
            }

            var deviceIdentifier = request.Target?.DeviceIdentifier
                                   ?? throw new InvalidOperationException("A target is required.");
            lock (stateGate)
            {
                activeRuns++;
                maximumActiveRuns = Math.Max(maximumActiveRuns, activeRuns);
                assignments.Add($"{request.TestId}:{deviceIdentifier}");
            }

            await Task.Delay(25, cancellationToken);
            lock (stateGate)
            {
                activeRuns--;
            }

            var test = Assert.Single(catalog.Tests, candidate => candidate.TestId == request.TestId);
            var target = new WorkspaceTestTarget(
                DevicePlatforms.Ios,
                deviceIdentifier,
                deviceIdentifier,
                test.AppId,
                DeviceStarted: false,
                ApplicationInstalled: false,
                ApplicationLaunched: true);
            return new WorkspaceTestRunResult(
                true,
                "Passed.",
                test,
                $"session-{deviceIdentifier}",
                null,
                [],
                target);
        }
    }

    [Fact]
    public async Task RunAllAsync_CountsCapabilitySkipsWithoutStoppingTheBatch()
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath, "a-unsupported", "b-supported");
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var executedTestIds = new List<string>();
        var runner = new WorkspaceTestBatchRunner(
            RunTestAsync,
            new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        var result = await runner.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            ContinueAfterTestFailure: false,
            EnableWorkspaceTools: false));

        Assert.Equal(["a-unsupported", "b-supported"], executedTestIds);
        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(1, result.SkippedCount);

        Task<WorkspaceTestRunResult> RunTestAsync(
            WorkspaceTestRunRequest request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            executedTestIds.Add(request.TestId);
            var test = Assert.Single(catalog.Tests, candidate => candidate.TestId == request.TestId);
            return Task.FromResult(request.TestId == "a-unsupported"
                ? WorkspaceTestRunResult.Skipped("Unsupported target.", test)
                : new WorkspaceTestRunResult(true, "Passed.", test, "session", null, []));
        }
    }

    [Fact]
    public async Task RunAllAsync_ParallelStopOnFailureFinishesActiveTestsAndSkipsPendingTests()
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath, "first", "second", "third");
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var targets = new[]
        {
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-one"),
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-two")
        };
        var runner = new WorkspaceTestBatchRunner(
            RunTestAsync,
            new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        var result = await runner.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            ContinueAfterTestFailure: false,
            EnableWorkspaceTools: false)
        {
            Targets = targets,
            Parallel = true
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(["first", "second"], result.Results.Select(run => run.Test?.TestId));

        async Task<WorkspaceTestRunResult> RunTestAsync(
            WorkspaceTestRunRequest request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var startParticipant = request.StartParticipant;
            if (startParticipant is not null)
            {
                await startParticipant.ArriveAndWaitAsync(cancellationToken);
            }

            var deviceIdentifier = request.Target?.DeviceIdentifier
                                   ?? throw new InvalidOperationException("A target is required.");
            var test = Assert.Single(catalog.Tests, candidate => candidate.TestId == request.TestId);
            var target = new WorkspaceTestTarget(
                DevicePlatforms.Ios,
                deviceIdentifier,
                deviceIdentifier,
                test.AppId,
                DeviceStarted: false,
                ApplicationInstalled: false,
                ApplicationLaunched: true);
            if (request.TestId == "first")
            {
                return WorkspaceTestRunResult.Failure(
                    "Failed.",
                    test,
                    $"session-{deviceIdentifier}",
                    target: target);
            }

            await Task.Delay(75, cancellationToken);
            return new WorkspaceTestRunResult(
                true,
                "Passed.",
                test,
                $"session-{deviceIdentifier}",
                null,
                [],
                target);
        }
    }

    [Fact]
    public async Task RunAllAsync_RunsTargetsConcurrentlyAndWaitsBeforeStartingTheNextTest()
    {
        using var environment = new TestEnvironment();
        var workspacePath = CreateWorkspace(environment.RootPath);
        var catalog = WorkspaceTestCatalog.Load(workspacePath);
        var firstTestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTestStarted = 0;
        var secondTestStarted = 0;
        var firstTestCompleted = 0;
        var secondTestStartedBeforeFirstCompleted = 0;
        var targets = new[]
        {
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-one"),
            new WorkspaceTestTargetRequest(DeviceIdentifier: "device-two")
        };
        var runner = new WorkspaceTestBatchRunner(
            RunTestAsync,
            new WorkspaceTestBatchAuditStore(environment.ApplicationPaths));

        var result = await runner.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            TestIds: ["first", "second"],
            EnableWorkspaceTools: false)
        {
            Targets = targets
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Parallel);
        Assert.Equal(4, result.PassedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, firstTestStarted);
        Assert.Equal(2, secondTestStarted);
        Assert.Equal(0, secondTestStartedBeforeFirstCompleted);
        Assert.Equal(
            ["first:device-one", "first:device-two", "second:device-one", "second:device-two"],
            result.Results.Select(run => $"{run.Test?.TestId}:{run.Target?.DeviceIdentifier}"));

        async Task<WorkspaceTestRunResult> RunTestAsync(
            WorkspaceTestRunRequest request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            using var startParticipant = request.StartParticipant;
            var deviceIdentifier = request.Target?.DeviceIdentifier
                                   ?? throw new InvalidOperationException("A target is required.");
            Task releaseTask;
            if (request.TestId == "first")
            {
                if (Interlocked.Increment(ref firstTestStarted) == targets.Length)
                {
                    firstTestRelease.TrySetResult();
                }
                releaseTask = firstTestRelease.Task;
            }
            else
            {
                if (Volatile.Read(ref firstTestCompleted) != targets.Length)
                {
                    Interlocked.Exchange(ref secondTestStartedBeforeFirstCompleted, 1);
                }
                if (Interlocked.Increment(ref secondTestStarted) == targets.Length)
                {
                    secondTestRelease.TrySetResult();
                }
                releaseTask = secondTestRelease.Task;
            }

            if (startParticipant is not null)
            {
                await startParticipant.ArriveAndWaitAsync(cancellationToken);
            }
            await releaseTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            if (request.TestId == "first")
            {
                Interlocked.Increment(ref firstTestCompleted);
            }

            var test = Assert.Single(catalog.Tests, candidate => candidate.TestId == request.TestId);
            var target = new WorkspaceTestTarget(
                DevicePlatforms.Ios,
                deviceIdentifier,
                deviceIdentifier,
                test.AppId,
                DeviceStarted: false,
                ApplicationInstalled: false,
                ApplicationLaunched: true);
            return new WorkspaceTestRunResult(
                true,
                "Passed.",
                test,
                $"session-{deviceIdentifier}",
                null,
                [],
                target);
        }
    }

    private static string CreateWorkspace(string rootPath, params string[] requestedTestIds)
    {
        var workspacePath = Path.Combine(rootPath, "workspace");
        var testsPath = Path.Combine(workspacePath, "ansight", "tests");
        Directory.CreateDirectory(testsPath);
        var testIds = requestedTestIds.Length == 0
            ? ["first", "second"]
            : requestedTestIds;
        foreach (var testId in testIds)
        {
            File.WriteAllText(
                Path.Combine(testsPath, $"{testId}.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "name": "{{testId}} test",
                  "appId": "com.example.target",
                  "prompt": "Run {{testId}}.",
                  "validation": "Verify {{testId}}."
                }
                """);
        }
        return workspacePath;
    }
}
