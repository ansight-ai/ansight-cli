using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceTestServiceTests
{
    [Fact]
    public void RunnerPromptRendersEveryOptionalEmbeddedSection()
    {
        var test = new WorkspaceTestDefinition(
            "checkout",
            "Checkout",
            "com.example.target",
            "Complete checkout.",
            new WorkspaceTestValidation(
                "The receipt is visible.",
                ["The order number is present."]),
            ["test-account"],
            "/workspace/ansight/tests/checkout.json");

        var prompt = test.BuildRunnerPrompt();

        Assert.Contains("Test scenario:\nComplete checkout.", prompt, StringComparison.Ordinal);
        Assert.Contains("Validation:\nThe receipt is visible.", prompt, StringComparison.Ordinal);
        Assert.Contains("- The order number is present.", prompt, StringComparison.Ordinal);
        Assert.Contains("- test-account", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("$SCENARIO$", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogReportsExpandedInstructionsOverTheRunnerLimit()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"ansight-workspace-{Guid.NewGuid():N}");
        var testsPath = Path.Combine(rootPath, "ansight", "tests");
        Directory.CreateDirectory(testsPath);
        try
        {
            var longPrompt = new string('x', SimulatorAgentService.MaximumInstructionCharacters);
            File.WriteAllText(
                Path.Combine(testsPath, "oversized.json"),
                $$"""
                {
                  "schemaVersion": 1,
                  "name": "Oversized",
                  "appId": "com.example.target",
                  "prompt": "{{longPrompt}}",
                  "validation": { "assertions": ["The test passes"] }
                }
                """);

            var catalog = WorkspaceTestCatalog.Load(rootPath);

            Assert.Single(catalog.Tests);
            Assert.Contains(catalog.Warnings, warning =>
                warning.Contains("Expanded runner instruction", StringComparison.Ordinal)
                && warning.Contains("4,000", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WhenSessionTimesOut_StopsLaunchedApplication()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);
        var targetLauncher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);
        var progress = new RecordingProgress<WorkspaceTestRunProgress>();

        var result = await service.RunAsync(
            new WorkspaceTestRunRequest(
                workspacePath,
                "example",
                SessionWaitTimeout: TimeSpan.Zero,
                Target: new WorkspaceTestTargetRequest(DeviceIdentifier: target.DeviceIdentifier),
                EnableWorkspaceTools: false),
            progress,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Timed out", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, targetLauncher.StopCallCount);
        Assert.Same(target, targetLauncher.StoppedTarget);
        Assert.Contains(progress.Values, value =>
            value.Stage == "app.stop"
            && value.Message.Contains("after the test", StringComparison.Ordinal)
            && value.Message.StartsWith("Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_RejectsADisabledTestBeforeLaunchingTheApplication()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        var testPath = Path.Combine(workspacePath, "ansight", "tests", "example.json");
        File.WriteAllText(
            testPath,
            File.ReadAllText(testPath).Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n              \"enabled\": false,",
                StringComparison.Ordinal));
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);
        var targetLauncher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);

        var result = await service.RunAsync(
            new WorkspaceTestRunRequest(workspacePath, "example"),
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, targetLauncher.LaunchCallCount);
    }

    [Fact]
    public async Task RunAsync_SkipsAnExplicitTargetOutsideTheTaskMetadataBeforeLaunch()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        AddTaskTargetMetadata(workspacePath, "ios", "virtual");
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);
        var targetLauncher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);

        var result = await service.RunAsync(
            new WorkspaceTestRunRequest(
                workspacePath,
                "example",
                Target: new WorkspaceTestTargetRequest(
                    DevicePlatforms.Android,
                    target.DeviceIdentifier,
                    DeviceKind: DeviceKinds.Virtual)),
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsSkipped);
        Assert.Contains("does not support target", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, targetLauncher.LaunchCallCount);
    }

    [Fact]
    public async Task RunAsync_RejectsATestWhoseDeclaredTaskCannotBeLoaded()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        var testPath = Path.Combine(workspacePath, "ansight", "tests", "example.json");
        File.WriteAllText(
            testPath,
            File.ReadAllText(testPath).Replace(
                "\"appId\": \"com.example.target\",",
                "\"appId\": \"com.example.target\",\n              \"taskId\": \"missing\",",
                StringComparison.Ordinal));
        var targetLauncher = new FakeTargetLauncher(new WorkspaceTestTarget(
            DevicePlatforms.Ios,
            "simulator-1",
            "iPhone Simulator",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true));
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);

        var result = await service.RunAsync(
            new WorkspaceTestRunRequest(workspacePath, "example", EnableWorkspaceTools: false),
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSkipped);
        Assert.Contains("references repository task 'missing'", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, targetLauncher.LaunchCallCount);
    }

    [Fact]
    public async Task RunAsync_AppliesSingleTaskTargetMetadataDuringAutomaticSelection()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        AddTaskTargetMetadata(workspacePath, "ios", "virtual");
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Ios,
            "simulator-1",
            "iPhone Simulator",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true)
        {
            DeviceKind = DeviceKinds.Virtual
        };
        var targetLauncher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);

        var result = await service.RunAsync(
            new WorkspaceTestRunRequest(
                workspacePath,
                "example",
                SessionWaitTimeout: TimeSpan.Zero,
                EnableWorkspaceTools: false),
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DevicePlatforms.Ios, targetLauncher.LastRequest?.Platform);
        Assert.Equal(DeviceKinds.Virtual, targetLauncher.LastRequest?.DeviceKind);
    }

    [Fact]
    public async Task LaunchAppAndWaitForSessionAsync_WhenSessionTimesOut_LeavesTheAppRunning()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);
        var targetLauncher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, targetLauncher);

        var result = await service.LaunchAppAndWaitForSessionAsync(
            target.ApplicationIdentifier,
            new WorkspaceTestTargetRequest(DeviceIdentifier: target.DeviceIdentifier),
            sessionWaitTimeout: TimeSpan.Zero,
            cancellationToken: CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("did not connect", result.Message, StringComparison.Ordinal);
        Assert.Equal("timed-out", Assert.Single(result.StartupSteps).Status);
        Assert.Equal("Wait for app to connect to Ansight", result.StartupSteps[0].Name);
        Assert.Equal(1, targetLauncher.LaunchCallCount);
        Assert.Equal(0, targetLauncher.StopCallCount);
        Assert.Same(target, result.Target);
    }

    [Fact]
    public async Task ReservedLaunchTransfersCleanupTargetBeforeSessionTimeout()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var target = new WorkspaceTestTarget("ios", "DEVICE-1", "iPhone", "com.example.app", false, false, true);
        var launcher = new FakeTargetLauncher(target);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, launcher);
        WorkspaceTestTarget? cleanupTarget = null;

        var result = await service.LaunchAppAndWaitForReservedSessionAsync(
            target.ApplicationIdentifier, null, null, TimeSpan.Zero, null, CancellationToken.None,
            onTargetLaunched: value => cleanupTarget = value);

        Assert.False(result.IsSuccess);
        Assert.Same(target, cleanupTarget);
        Assert.Same(target, result.Target);
        Assert.Equal(0, launcher.StopCallCount);
    }

    [Fact]
    public async Task ReservedLaunchTransfersCleanupTargetBeforeSessionWaitIsCancelled()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        using var cancellation = new CancellationTokenSource();
        var target = new WorkspaceTestTarget("android", "DEVICE-1", "Pixel", "com.example.app", false, false, true);
        var service = new WorkspaceTestService(runtime, runtime.SimulatorAgent, new FakeTargetLauncher(target));
        WorkspaceTestTarget? cleanupTarget = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LaunchAppAndWaitForReservedSessionAsync(
            target.ApplicationIdentifier, null, null, TimeSpan.FromSeconds(45), null, cancellation.Token,
            onTargetLaunched: value =>
            {
                cleanupTarget = value;
                cancellation.Cancel();
            }));

        Assert.Same(target, cleanupTarget);
    }

    [Fact]
    public async Task RunAllAsync_PersistsAnInspectableBatchIncludingPreAgentFailures()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var workspacePath = CreateWorkspace(environment.RootPath);
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);
        var service = new WorkspaceTestService(
            runtime,
            runtime.SimulatorAgent,
            new FakeTargetLauncher(target));

        var result = await service.Batches.RunAllAsync(new WorkspaceTestBatchRequest(
            workspacePath,
            SessionWaitTimeout: TimeSpan.Zero,
            Target: new WorkspaceTestTargetRequest(DeviceIdentifier: target.DeviceIdentifier),
            EnableWorkspaceTools: false)
        {
            Source = "test"
        });

        Assert.NotNull(result.BatchRunId);
        Assert.NotNull(result.HistoryFilePath);
        Assert.Null(result.HistoryPersistenceError);
        Assert.Equal(1, result.FailedCount);
        var history = service.History.List(workspacePath: workspacePath);
        var batch = Assert.Single(history.Batches);
        Assert.Equal(result.BatchRunId, batch.Audit.BatchRunId);
        Assert.Equal("test", batch.Audit.Source);
        Assert.Equal(WorkspaceTestHistoryStatuses.Failed, batch.Audit.Status);
        var item = Assert.Single(batch.Audit.Tests);
        Assert.Equal(WorkspaceTestHistoryStatuses.Failed, item.Status);
        Assert.Null(item.AgentRunId);
        var inspection = service.History.Inspect(result.BatchRunId!);
        Assert.NotNull(inspection);
        Assert.Empty(inspection.Runs);
        Assert.Equal(result.BatchRunId, inspection.Batch!.Audit.BatchRunId);
    }

    [Fact]
    public void HistoryGroupsStandaloneRunsByProvidedBatchRunId()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var startedUtc = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        var auditStore = new AuditStore(runtime.ApplicationPaths);
        Assert.Null(auditStore.Save(CreateHistoryAudit(
            "run-one",
            "release_42",
            startedUtc)).ErrorMessage);
        Assert.Null(auditStore.Save(CreateHistoryAudit(
            "run-two",
            "release_42",
            startedUtc.AddMinutes(1))).ErrorMessage);
        Assert.Null(auditStore.Save(CreateHistoryAudit(
            "unrelated",
            "release_43",
            startedUtc.AddMinutes(2))).ErrorMessage);

        var history = runtime.WorkspaceTests.History.List(batchRunId: "release_42");
        var inspection = runtime.WorkspaceTests.History.Inspect("release_42");

        Assert.Empty(history.Batches);
        Assert.Equal(["run-two", "run-one"], history.Runs.Select(static run => run.RunId));
        Assert.NotNull(inspection);
        Assert.Null(inspection.Batch);
        Assert.Equal(["run-one", "run-two"], inspection.Runs.Select(static run => run.Audit.RunId));
    }

    [Fact]
    public void PinBatchTarget_WhenTheFirstRunResolvesATarget_PinsItsExactDevice()
    {
        var resolvedTarget = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: true,
            ApplicationInstalled: true,
            ApplicationLaunched: true);

        var result = WorkspaceTestService.PinBatchTarget(null, resolvedTarget);

        Assert.NotNull(result);
        Assert.Equal(DevicePlatforms.Android, result.Platform);
        Assert.Equal("android-1", result.DeviceIdentifier);
        Assert.Null(result.ApplicationPath);
        Assert.Null(result.DeviceKind);
    }

    [Fact]
    public void PinBatchTarget_WhenTheRequestAlreadySelectsATarget_PreservesTheRequest()
    {
        var requestedTarget = new WorkspaceTestTargetRequest(
            Platform: DevicePlatforms.Ios,
            DeviceIdentifier: "ios-1",
            ApplicationPath: "/tmp/Target.app",
            DeviceKind: DeviceKinds.Virtual);
        var resolvedTarget = new WorkspaceTestTarget(
            DevicePlatforms.Ios,
            "ios-1",
            "iPhone",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);

        var result = WorkspaceTestService.PinBatchTarget(requestedTarget, resolvedTarget);

        Assert.Same(requestedTarget, result);
    }

    [Fact]
    public void PinBatchTarget_PreservesHeadlessWhenPinningAnAutomaticallyResolvedDevice()
    {
        var requestedTarget = new WorkspaceTestTargetRequest(Headless: true);
        var resolvedTarget = new WorkspaceTestTarget(
            DevicePlatforms.Ios,
            "ios-1",
            "iPhone",
            "com.example.target",
            DeviceStarted: true,
            ApplicationInstalled: false,
            ApplicationLaunched: true);

        var result = WorkspaceTestService.PinBatchTarget(requestedTarget, resolvedTarget);

        Assert.NotNull(result);
        Assert.Equal("ios-1", result.DeviceIdentifier);
        Assert.True(result.Headless);
    }

    [Fact]
    public void SessionConnectionTracker_RequiresConsecutiveSamplesForTheSameSession()
    {
        var tracker = new WorkspaceSessionConnectionTracker(requiredSamples: 2);

        Assert.False(tracker.Observe("session-1"));
        tracker.Reset();
        Assert.False(tracker.Observe("session-1"));
        Assert.False(tracker.Observe("session-2"));
        Assert.True(tracker.Observe("session-2"));
    }

    [Fact]
    public void MissingTestMessageSuggestsTheClosestIdAndListsAvailableTests()
    {
        var catalog = new WorkspaceTestCatalogResult(
            "/workspace",
            [
                CreateTestDefinition("login", "Login"),
                CreateTestDefinition("map-search", "Map search"),
                CreateTestDefinition("view-3d-guide", "View 3D guide")
            ],
            []);

        var message = WorkspaceTestService.BuildMissingTestMessage(catalog, "open-3d-guide");

        Assert.Contains("Did you mean 'view-3d-guide'?", message, StringComparison.Ordinal);
        Assert.Contains(
            "Available tests: login, map-search, view-3d-guide.",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MatchWorkspaceTest_RecoversTheNamedTestFromAnOlderRunInstruction()
    {
        var login = CreateTestDefinition("login", "Login to Red-Point");
        var other = CreateTestDefinition("map-search", "Search the map") with
        {
            Prompt = "Search for the requested map location."
        };

        var match = WorkspaceTestHistoryService.MatchWorkspaceTest(
            [login, other],
            login.AppId,
            login.BuildRunnerPrompt());

        Assert.Same(login, match);
    }

    [Fact]
    public void BuildRunDisplayName_UsesAConciseInstructionTitleForAStandaloneRun()
    {
        var displayName = WorkspaceTestHistoryService.BuildRunDisplayName(
            null,
            null,
            "Test scenario:\nVerify that the selected item remains visible.");

        Assert.Equal(
            "Verify that the selected item remains visible.",
            displayName);
    }

    [Fact]
    public void FilterBatchHistoryEntry_ScopesTestsCountsAndStatusToTheRequestedApp()
    {
        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var entry = new WorkspaceTestBatchRunHistoryEntry(
            new WorkspaceTestBatchRunAudit(
                1,
                "batch-1",
                "test",
                "/workspace",
                ["alpha", "bravo"],
                "gpt-5.6-terra",
                4,
                4,
                8,
                true,
                true,
                null,
                null,
                startedUtc,
                startedUtc.AddSeconds(20),
                WorkspaceTestHistoryStatuses.Failed,
                "One test failed.",
                [
                    new WorkspaceTestBatchItemAudit(
                        1,
                        "alpha",
                        "Alpha test",
                        "com.example.alpha",
                        WorkspaceTestHistoryStatuses.Succeeded,
                        "Passed.",
                        "run-alpha",
                        "session-alpha",
                        null,
                        null,
                        10,
                        100,
                        1,
                        1),
                    new WorkspaceTestBatchItemAudit(
                        2,
                        "bravo",
                        "Bravo test",
                        "com.example.bravo",
                        WorkspaceTestHistoryStatuses.Failed,
                        "Failed.",
                        "run-bravo",
                        "session-bravo",
                        null,
                        null,
                        20,
                        200,
                        2,
                        2)
                ]),
            "/history/batch-1.json");

        var filtered = WorkspaceTestHistoryService.FilterBatchHistoryEntry(
            entry,
            "com.example.alpha");

        var test = Assert.Single(filtered.Audit.Tests);
        Assert.Equal(1, test.Index);
        Assert.Equal("alpha", test.TestId);
        Assert.Equal(WorkspaceTestHistoryStatuses.Succeeded, filtered.Audit.Status);
        Assert.Equal(1, filtered.Audit.PassedCount);
        Assert.Equal(0, filtered.Audit.FailedCount);
        Assert.Equal(100, filtered.Audit.TotalTokens);
        Assert.Equal(["alpha"], filtered.Audit.RequestedTestIds);
    }

    private static string CreateWorkspace(string rootPath)
    {
        var workspacePath = Path.Combine(rootPath, "workspace");
        var testsPath = Path.Combine(workspacePath, "ansight", "tests");
        Directory.CreateDirectory(testsPath);
        File.WriteAllText(
            Path.Combine(testsPath, "example.json"),
            """
            {
              "schemaVersion": 1,
              "name": "Example test",
              "appId": "com.example.target",
              "prompt": "Run the example test.",
              "validation": {
                "assertions": ["The example assertion passes"]
              }
            }
            """);
        return workspacePath;
    }

    private static void AddTaskTargetMetadata(string workspacePath, string platform, string deviceKind)
    {
        var testPath = Path.Combine(workspacePath, "ansight", "tests", "example.json");
        File.WriteAllText(
            testPath,
            File.ReadAllText(testPath).Replace(
                "\"appId\": \"com.example.target\",",
                "\"appId\": \"com.example.target\",\n              \"taskId\": \"example\",",
                StringComparison.Ordinal));
        var tasksPath = Path.Combine(workspacePath, "ansight", "tasks");
        Directory.CreateDirectory(tasksPath);
        File.WriteAllText(
            Path.Combine(tasksPath, "example.ts"),
            $$"""
            export const task = {
              "schemaVersion": 1,
              "appId": "com.example.target",
              "title": "Example task",
              "description": "Runs the example test.",
              "platforms": ["{{platform}}"],
              "deviceKinds": ["{{deviceKind}}"]
            };

            export default async function run({ expect }) {
              expect(true, { id: "example" }).toBe(true);
            }
            """);
    }

    private static WorkspaceTestDefinition CreateTestDefinition(string testId, string name)
        => new(
            testId,
            name,
            "com.example.target",
            "Run the example test.",
            new WorkspaceTestValidation(string.Empty, []),
            [],
            $"/workspace/ansight/tests/{testId}.json");

    private static SimulatorAgentRunAudit CreateHistoryAudit(
        string runId,
        string batchRunId,
        DateTimeOffset startedUtc)
        => new(
            1,
            runId,
            $"session-{runId}",
            "test-model",
            SimulatorAgentRunStatus.Succeeded,
            "Completed.",
            startedUtc,
            startedUtc.AddSeconds(1),
            1_000,
            12,
            60,
            1,
            1,
            0,
            0,
            1,
            0,
            0,
            0,
            0,
            0,
            new SimulatorAgentTokenUsage(100, 10, 110, 80, 0, 2),
            ["Verify history."],
            [new SimulatorAgentInstructionResult(
                1,
                "Verify history.",
                SimulatorAgentInstructionStatus.Succeeded,
                "Done.",
                1,
                0)],
            [],
            [])
        {
            AppId = "com.example.target",
            WorkspacePath = "/workspace",
            WorkspaceTestId = runId,
            WorkspaceTestName = runId,
            BatchRunId = batchRunId
        };

    private sealed class FakeTargetLauncher(WorkspaceTestTarget target) : IWorkspaceTestTargetLauncher
    {
        public int LaunchCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public WorkspaceTestTarget? StoppedTarget { get; private set; }

        public WorkspaceTestTargetRequest? LastRequest { get; private set; }

        public Task<WorkspaceTestTargetLaunchResult> LaunchAsync(
            string applicationIdentifier,
            WorkspaceTestTargetRequest? request,
            IProgress<WorkspaceTestRunProgress>? progress,
            CancellationToken cancellationToken)
        {
            LaunchCallCount++;
            LastRequest = request;
            return Task.FromResult(WorkspaceTestTargetLaunchResult.Success(target));
        }

        public Task<DeviceOperationResult> StopAsync(
            WorkspaceTestTarget targetToStop,
            CancellationToken cancellationToken)
        {
            StopCallCount++;
            StoppedTarget = targetToStop;
            return Task.FromResult(DeviceOperationResult.Success(
                "terminate-app",
                targetToStop.Platform,
                targetToStop.DeviceIdentifier,
                "Stopped."));
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }
}
