using System.IO.Compression;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces.Catalog;
using Ansight.Host.Workspaces.Cloud;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class WorkspaceTaskTestTests
{
    [Theory]
    [InlineData("yaml", "appId: demo\ntaskId: checkout\ntaskInput:\n  count: 2\n  enabled: true")]
    [InlineData("json", "{\"appId\":\"demo\",\"taskId\":\"checkout\",\"taskInput\":{\"count\":2,\"enabled\":true}}")]
    public void TaskOnlyDefinitionsNeedNoPrompt(string extension, string source)
    {
        var test = WorkspaceTestCatalog.Parse("ansight/tests", $"ansight/tests/checkout.{extension}", source);
        Assert.True(test.IsTaskOnly);
        Assert.Empty(test.Prompt);
        Assert.Empty(test.BuildRunnerPrompt());
        Assert.Equal(2, test.TaskInput!["count"]!.GetValue<int>());
        Assert.True(test.TaskInput["enabled"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("taskId: ''")]
    [InlineData("taskId: checkout\nprompt: ''")]
    [InlineData("taskId: checkout\nvalidation: Check the receipt.")]
    [InlineData("taskId: checkout\nvalidation:\n  assertions: [Receipt visible]")]
    [InlineData("taskId: checkout\nhintTasks: []")]
    [InlineData("taskId: checkout\ntaskInput: []")]
    [InlineData("taskId: checkout\ntaskInput: null")]
    [InlineData("taskId: checkout\nprompt: Check the receipt.\ntaskInput: {}")]
    public void InvalidTaskOnlyDefinitionsAreRejected(string fields)
        => Assert.Throws<InvalidDataException>(() => WorkspaceTestCatalog.Parse(
            "ansight/tests", "ansight/tests/checkout.yaml", "appId: demo\n" + fields));

    [Fact]
    public void PromptWithTaskRetainsAgentBehavior()
    {
        var test = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/checkout.yaml",
            "appId: demo\ntaskId: checkout\nprompt: Check the receipt.");
        Assert.False(test.IsTaskOnly);
        Assert.Contains("Run repository task 'checkout'.", test.BuildRunnerPrompt());
        Assert.Contains("Check the receipt.", test.BuildRunnerPrompt());
    }

    [Fact]
    public void TaskInputsPreserveNestedJsonTypesAndQuotedStrings()
    {
        var test = WorkspaceTestCatalog.Parse("ansight/tests", "ansight/tests/checkout.yaml", """
            appId: demo
            taskId: checkout
            taskInput:
              values: [1, false, null, "1", "false", "null", 1.25]
              nested: { enabled: "yes", schemaVersion: "custom" }
            """);
        var input = test.TaskInput!;
        Assert.Equal(1, input["values"]![0]!.GetValue<int>());
        Assert.False(input["values"]![1]!.GetValue<bool>());
        Assert.Null(input["values"]![2]);
        Assert.Equal("1", input["values"]![3]!.GetValue<string>());
        Assert.Equal("false", input["values"]![4]!.GetValue<string>());
        Assert.Equal("null", input["values"]![5]!.GetValue<string>());
        Assert.Equal(1.25m, input["values"]![6]!.GetValue<decimal>());
        Assert.Equal("yes", input["nested"]!["enabled"]!.GetValue<string>());
        Assert.Equal("custom", input["nested"]!["schemaVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("expect(input.count, { id: 'count' }).toBe(2);", RepositoryTaskRunStatus.Passed)]
    [InlineData("expect(input.count, { id: 'count' }).toBe(3);", RepositoryTaskRunStatus.Failed)]
    [InlineData("return {};", RepositoryTaskRunStatus.Inconclusive)]
    [InlineData("throw new Error('task failed');", RepositoryTaskRunStatus.Error)]
    public async Task RunsRealTaskWithoutCloudAndPersistsHistory(string body, RepositoryTaskRunStatus status)
    {
        using var fixture = new Fixture(body);
        var result = await fixture.Service.RunAsync(fixture.Request);
        Assert.True(result.TaskResult is not null, result.Message);
        Assert.Equal(status, result.TaskResult.Status);
        Assert.Equal(status == RepositoryTaskRunStatus.Passed, result.IsSuccess);
        Assert.Null(result.AgentResult);
        Assert.Equal("task", result.TaskAudit?.ExecutionKind);
        Assert.True(result.TaskAudit!.TraceEnabled);
        Assert.Equal(0, result.TaskAudit!.ModelPassCount);
        Assert.Equal(0, result.TaskAudit.Tokens.TotalTokens);
        Assert.Empty(result.TaskAudit.Instructions);
        Assert.Equal(0, fixture.Gateway.Calls);
        Assert.True(fixture.Runtime.IsSessionLive(fixture.Session.SessionId));
        using var claim = fixture.Service.ReserveSession(fixture.Session);
        var history = Assert.Single(fixture.Service.History.List(workspacePath: fixture.Workspace).Runs);
        Assert.Equal("task", history.ExecutionKind);
        Assert.Equal(result.TaskResult!.RunId, history.RunId);
        var saved = Assert.Single(fixture.Service.History.Inspect(history.RunId)!.Runs).Audit;
        Assert.Equal(status, saved.TaskResult!.Status);
        Assert.Equal(result.TaskResult.Assertions.Count, saved.TaskResult.Assertions.Count);
        Assert.NotNull(saved.TaskResult.SourceTrace);

        var exportPath = Path.Combine(fixture.Workspace, "trace.zip");
        var export = await fixture.Service.TraceExports.ExportAsync(history.RunId, exportPath);
        Assert.True(export.IsSuccess, export.Message);
        Assert.Equal(1, export.TraceCount);
        using var archive = ZipFile.OpenRead(exportPath);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith(".json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("taskId: missing", "not loaded")]
    [InlineData("taskId: example\ntaskInput: { count: wrong }", "count")]
    [InlineData("taskId: example\nrequiredSecrets: [missing.password]", "missing")]
    public async Task InvalidConfigurationFailsBeforeLaunch(string fields, string expectedMessage)
    {
        using var fixture = new Fixture("expect(true).toBe(true);");
        fixture.WriteTest(fields);
        var result = await fixture.Service.RunAsync(fixture.Request with { SessionId = null, Target = fixture.TargetRequest });
        Assert.False(result.IsSuccess);
        Assert.Null(result.TaskResult);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Launcher.Launches);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task RequiredSecretsReachTaskWithoutAppearingInAudit()
    {
        using var fixture = new Fixture("expect(secrets.require('login.password').length, { id: 'secret' }).toBe(16);");
        fixture.WriteTest("taskId: example\nrequiredSecrets: [login.password]");
        fixture.Runtime.SimulatorAgent.SetTestSecret("test.tasks", "login.password", "private-password");
        var result = await fixture.Service.RunAsync(fixture.Request);
        Assert.True(result.IsSuccess, result.Message);
        Assert.DoesNotContain("private-password", File.ReadAllText(result.TaskAuditFilePath!));
    }

    [Fact]
    public async Task TaskOnlyRunsWhenNoCloudGatewayIsConfigured()
    {
        using var fixture = new Fixture("expect(input.count, { id: 'default-input' }).toBe(2);");
        fixture.WriteTest("taskId: example"); // The task's input default is applied.
        var service = new WorkspaceTestService(fixture.Runtime, fixture.Runtime.SimulatorAgent, fixture.Launcher);
        var result = await service.RunAsync(fixture.Request);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(result.AgentResult);
    }

    [Fact]
    public async Task DirectTaskRecordsHostToolCallsAndTheirTrace()
    {
        using var fixture = new Fixture("""
            const capabilities = await ansight.capabilities.snapshot();
            expect(capabilities.executionMode, { id: 'capture-mode' }).toBe('device');
            """);
        var result = await fixture.Service.RunAsync(fixture.Request with { CaptureTrace = true });
        Assert.True(result.IsSuccess, result.Message);
        var call = Assert.Single(result.TaskResult!.ToolCalls);
        Assert.NotNull(call.Arguments);
        Assert.NotNull(call.Result);
        Assert.Equal(1, result.RunAudit!.AnsightToolCallCount);
        Assert.Empty(result.RunAudit.ModelPasses);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Theory]
    [InlineData("\"enabled\": false,", "disabled", false)]
    [InlineData("\"platforms\": [\"ios\"],", "skipped", true)]
    public async Task DisabledOrIncompatibleTaskDoesNotLaunch(string descriptor, string message, bool skipped)
    {
        using var fixture = new Fixture("expect(true).toBe(true);", descriptor);
        var result = await fixture.Service.RunAsync(fixture.Request with { SessionId = null, Target = fixture.TargetRequest });
        Assert.False(result.IsSuccess);
        Assert.Equal(skipped, result.IsSkipped);
        Assert.Contains(message, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Launcher.Launches);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task TaskCapabilityPreflightRejectsBeforeImportingCode()
    {
        using var fixture = new Fixture("throw new Error('must not execute');",
            "\"requires\": { \"appTools\": [\"missing.tool\"] },");
        var result = await fixture.Service.RunAsync(fixture.Request);
        Assert.Equal(RepositoryTaskRunStatus.Rejected, result.TaskResult?.Status);
        Assert.Contains("missing.tool", result.Message);
        Assert.DoesNotContain("must not execute", result.Message);
        Assert.Equal(0, fixture.Gateway.Calls);
    }

    [Fact]
    public async Task TaskTimeoutFailsTheTest()
    {
        using var fixture = new Fixture("await new Promise(() => {});", "\"timeoutSeconds\": 1,");
        var result = await fixture.Service.RunAsync(fixture.Request);
        Assert.Equal(RepositoryTaskRunStatus.TimedOut, result.TaskResult?.Status);
        Assert.False(result.IsSuccess);
        Assert.False(result.WasCancelled);
        using var claim = fixture.Service.ReserveSession(fixture.Session);
    }

    [Fact]
    public async Task LaunchedTaskTestStopsAppAndBatchRetainsTaskRun()
    {
        using var fixture = new Fixture("expect(input.count, { id: 'count' }).toBe(2);");
        fixture.Launcher.OwnApp = true;
        var batch = await fixture.Service.Batches.RunAllAsync(new WorkspaceTestBatchRequest(
            fixture.Workspace, Target: fixture.TargetRequest));
        Assert.True(batch.PassedCount == 1, string.Join("; ", batch.Results.Select(result => result.Message)));
        Assert.Equal(1, fixture.Launcher.Launches);
        Assert.Equal(1, fixture.Launcher.Stops);
        Assert.Equal(0, fixture.Gateway.Calls);
        var inspection = fixture.Service.History.Inspect(batch.BatchRunId!)!;
        var item = Assert.Single(inspection.Batch!.Audit.Tests);
        Assert.Null(item.AgentRunId);
        Assert.Equal(batch.Results[0].TaskResult!.RunId, item.TaskRunId);
        Assert.Equal(item.TaskRunId, Assert.Single(inspection.Runs).Audit.RunId);
        Assert.Equal(0, item.ModelPassCount);
    }

    [Fact]
    public async Task CancellationReleasesSessionAndNeverCallsCloud()
    {
        using var fixture = new Fixture("await new Promise(() => {});");
        using var cancellation = new CancellationTokenSource();
        var progress = new DelegatingProgress<WorkspaceTestRunProgress>(value =>
        {
            if (value.Stage == "task.running") cancellation.CancelAfter(500);
        });
        var result = await fixture.Service.RunAsync(fixture.Request, progress, cancellation.Token);
        Assert.True(result.WasCancelled, result.Message);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, fixture.Gateway.Calls);
        using var claim = fixture.Service.ReserveSession(fixture.Session);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        public RuntimeCoordinator Runtime { get; }
        public AppSessionSnapshot Session { get; }
        public Launcher Launcher { get; }
        public RejectingGateway Gateway { get; } = new();
        public WorkspaceTestService Service { get; }
        public string Workspace => environment.RootPath;
        public WorkspaceTestTargetRequest TargetRequest { get; } = new(Platform: "android", DeviceIdentifier: "task-phone")
        { ExecutionMode = "device" };
        public WorkspaceTestRunRequest Request => new(Workspace, "example", Session.SessionId);

        public Fixture(string body, string descriptor = "")
        {
            var composition = new MefHostComposition(environment.ApplicationPaths,
                new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
            var state = composition.Get<IRuntimeState>();
            Runtime = composition.Get<RuntimeCoordinator>();
            var target = new WorkspaceTestTarget("android", "task-phone", "Phone", "test.tasks", false, false, false)
            { ExecutionMode = "device", DeviceKind = DeviceKinds.Physical };
            state.TryGetSessionSnapshot(state.CreateDeviceSession(target), out var session);
            Session = session!;
            Launcher = new Launcher(target, Session);
            Service = new WorkspaceTestService(Runtime, Runtime.SimulatorAgent, Launcher, Gateway);
            WriteTest("taskId: example\ntaskInput: { count: 2 }");
            var tasks = Path.Combine(Workspace, "ansight", "tasks");
            Directory.CreateDirectory(tasks);
            File.WriteAllText(Path.Combine(tasks, "example.ts"), $$"""
                export const task = {
                  "schemaVersion": 2, "appId": "test.tasks", "title": "Example", "description": "Checks the task runner",
                  {{descriptor}}
                  "inputSchema": { "type": "object", "properties": { "count": { "type": "integer", "default": 2 } } }
                };
                export default async ({ input, expect, secrets, ansight }) => { {{body}} };
                """);
        }

        public void WriteTest(string fields)
        {
            var tests = Path.Combine(Workspace, "ansight", "tests");
            Directory.CreateDirectory(tests);
            File.WriteAllText(Path.Combine(tests, "example.yaml"), "appId: test.tasks\n" + fields);
        }

        public void Dispose() { Runtime.Dispose(); environment.Dispose(); }
    }

    private sealed class Launcher(WorkspaceTestTarget target, AppSessionSnapshot session) : IWorkspaceTestTargetLauncher
    {
        public int Launches { get; private set; }
        public int Stops { get; private set; }
        public bool OwnApp { get; set; }
        public Task<WorkspaceTestTargetLaunchResult> LaunchAsync(string appId, WorkspaceTestTargetRequest? request,
            IProgress<WorkspaceTestRunProgress>? progress, CancellationToken cancellationToken)
        {
            Launches++;
            return Task.FromResult(WorkspaceTestTargetLaunchResult.Success(target with { ApplicationLaunched = OwnApp })
                with { ExistingSession = session });
        }
        public Task<DeviceOperationResult> StopAsync(WorkspaceTestTarget target, CancellationToken cancellationToken)
        {
            Stops++;
            return Task.FromResult(DeviceOperationResult.Success("terminate-app", target.Platform, target.DeviceIdentifier, "Stopped."));
        }
    }

    private sealed class RejectingGateway : IWorkspaceTestRunGateway
    {
        public int Calls { get; private set; }
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Task-only tests must never prepare a model or meter AI usage.");
        }
        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Task-only tests must never complete AI metering.");
    }
}
