using System.Diagnostics;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Trends;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryAutomationTriggerServiceTests
{
    [Fact]
    public void Load_WithOneModulePerTrigger_IndexesTheFileNameAndDeclaredDescriptor()
    {
        using var repository = CreateRepository(declareAppId: true);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Warnings);
        var trigger = Assert.Single(result.Catalog.Triggers);
        Assert.Equal("capture-error", trigger.TriggerId);
        Assert.Equal(1, trigger.SchemaVersion);
        Assert.Equal("com.example.app", trigger.AppId);
        Assert.Equal(AutomationEventNormalizer.SessionLogReceivedKind, trigger.EventKind);
        Assert.Equal("string", trigger.EventSchema?["properties"]?["priority"]?["type"]?.GetValue<string>());
        Assert.Equal("capture-error", trigger.Automation.AutomationId);
        Assert.Equal("typescript", trigger.Automation.ActionKindName);
        Assert.Equal(
            Path.Combine(repository.Path, "ansight", "triggers", "capture-error.ts"),
            trigger.Automation.ActionTarget);
        Assert.Equal(TimeSpan.FromMilliseconds(100), trigger.Automation.FunctionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), trigger.Automation.ActionTimeout);
    }

    [Fact]
    public void Load_DisabledTriggerRemainsDiscoverableButCannotMatchEvents()
    {
        using var repository = CreateRepository(declareAppId: true);
        var modulePath = GetTriggerPath(repository, "capture-error.ts");
        File.WriteAllText(
            modulePath,
            File.ReadAllText(modulePath).Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1,\n              \"enabled\": false,",
                StringComparison.Ordinal));

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Warnings);
        Assert.False(Assert.Single(result.Catalog.Triggers).Enabled);
        Assert.False(Assert.Single(result.Catalog.PublicTriggers).Enabled);
        Assert.False(result.Catalog.HasCandidates(
            AutomationEventNormalizer.SessionLogReceivedKind,
            "com.example.app"));
        Assert.Empty(result.Catalog.FindMatches(CreateEnvelope("com.example.app", "Error")));
    }

    [Fact]
    public void ContractBuilder_ExtractsTheTriggerEventSchemaAndRuntimeApi()
    {
        using var repository = CreateRepository(declareAppId: true);
        var loadResult = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        var contract = RepositoryModuleContractBuilder.BuildTrigger(
            Assert.Single(loadResult.Catalog.Triggers),
            includeDefinitions: true);

        Assert.Equal("trigger", contract["moduleType"]?.GetValue<string>());
        Assert.Equal("capture-error", contract["moduleId"]?.GetValue<string>());
        Assert.Equal("string", contract["schemas"]?["eventPayload"]?["properties"]?["priority"]?["type"]?.GetValue<string>());
        Assert.Equal(
            RepositoryModuleContractArtifacts.TriggerDefinitionSchemaId,
            contract["definitionSchema"]?["$id"]?.GetValue<string>());
        var typeDefinitions = contract["typeDefinitions"]?.GetValue<string>();
        Assert.Contains("TriggerFunction", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("MauiContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("ReactContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("FlutterContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("CapacitorContext", typeDefinitions, StringComparison.Ordinal);
        Assert.Contains("readonly artifacts", typeDefinitions, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsAnUnsupportedTriggerSchemaVersion()
    {
        using var repository = CreateRepository(declareAppId: true);
        var modulePath = GetTriggerPath(repository, "capture-error.ts");
        File.WriteAllText(
            modulePath,
            File.ReadAllText(modulePath).Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 3",
                StringComparison.Ordinal));

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Catalog.Triggers);
        Assert.Contains("schemaVersion must be 1", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_IgnoresEditorOnlyTypeDeclarationFiles()
    {
        using var repository = CreateRepository(declareAppId: true);
        File.WriteAllText(
            GetTriggerPath(repository, "ansight-trigger.d.ts"),
            "export type TriggerDefinition = unknown;");

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Warnings);
        Assert.Single(result.Catalog.Triggers);
    }

    [Fact]
    public void Load_IgnoresLegacyJavaScriptModuleExtensions()
    {
        using var repository = CreateRepository(declareAppId: true);
        var source = File.ReadAllText(GetTriggerPath(repository, "capture-error.ts"));
        File.WriteAllText(GetTriggerPath(repository, "legacy.mjs"), source);
        File.WriteAllText(GetTriggerPath(repository, "legacy.js"), source);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Warnings);
        Assert.Single(result.Catalog.Triggers);
    }

    [Fact]
    public void Load_WithHostAppScope_DoesNotRequireAppIdInsideEachFile()
    {
        using var repository = CreateRepository(declareAppId: false);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            "com.example.app");

        Assert.Empty(result.Warnings);
        Assert.Equal("com.example.app", Assert.Single(result.Catalog.Triggers).AppId);
    }

    [Fact]
    public void Load_WithJavaScriptPredicate_RejectsTheDescriptorWithoutExecutingIt()
    {
        using var repository = CreateRepository(declareAppId: true);
        var modulePath = GetTriggerPath(repository, "capture-error.ts");
        var source = File.ReadAllText(modulePath)
            .Replace(
                "\"conditions\": [",
                "\"predicate\": \"event => true\", \"conditions\": [",
                StringComparison.Ordinal);
        File.WriteAllText(modulePath, source);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Catalog.PublicTriggers);
        Assert.Contains("predicate", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithAnActionTimeoutOutsideTheHostLimit_RejectsTheTrigger()
    {
        using var repository = CreateRepository(declareAppId: true, actionTimeoutSeconds: 301);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Catalog.Triggers);
        Assert.Contains("between 1 and 300", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithAFunctionTimeoutOutsideTheTightHostLimit_RejectsTheTrigger()
    {
        using var repository = CreateRepository(declareAppId: true, functionTimeoutMs: 1_001);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        Assert.Empty(result.Catalog.Triggers);
        Assert.Contains("between 10 and 1000", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WithABoundedRetryPolicy_ExposesTheValidatedPolicy()
    {
        using var repository = CreateRepository(declareAppId: true, retryDescriptor: """
            ,
              "retry": {
                "maxAttempts": 3,
                "initialDelayMs": 25,
                "backoffMultiplier": 2,
                "maxDelayMs": 100
              }
            """);

        var result = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));

        var trigger = Assert.Single(result.Catalog.Triggers);
        Assert.Equal(3, trigger.Automation.RetryPolicy.MaximumAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(25), trigger.Automation.RetryPolicy.InitialDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(100), trigger.Automation.RetryPolicy.MaximumDelay);
    }

    [Fact]
    public async Task Publish_RetriesFailedAttemptsAndPersistsEveryAttemptTrace()
    {
        using var repository = CreateRepository(declareAppId: true, retryDescriptor: """
            ,
              "retry": {
                "maxAttempts": 3,
                "initialDelayMs": 10,
                "backoffMultiplier": 1,
                "maxDelayMs": 10
              }
            """);
        using var traceDirectory = TestDirectory.Create();
        var executor = new SequencedExecutor(
            AutomationRunStatus.Failed,
            AutomationRunStatus.Failed,
            AutomationRunStatus.Succeeded);
        var runStore = new RepositoryAutomationRunStore(traceDirectory.Path);
        await using var service = new RepositoryAutomationTriggerService(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            queueCapacity: 8,
            maximumConcurrentRuns: 1,
            executor,
            runStore: runStore);
        await service.StartAsync();
        var finalAttempt = new TaskCompletionSource<AutomationRunCompletedEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.RunCompleted += (_, run) =>
        {
            if (!run.WillRetry)
            {
                finalAttempt.TrySetResult(run);
            }
        };

        service.Publish(CreateEnvelope("com.example.app", "Error"));

        var completed = await finalAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, completed.AttemptNumber);
        Assert.Equal(AutomationRunStatus.Succeeded, completed.Status);
        var traces = new RepositoryAutomationRunStore(traceDirectory.Path)
            .GetRecent("com.example.app", 10);
        Assert.Equal([3, 2, 1], traces.Select(trace => trace.AttemptNumber));
        Assert.All(traces, trace => Assert.Equal(completed.RunId, trace.RunId));
        Assert.True(traces[1].WillRetry);
        Assert.True(traces[2].WillRetry);
    }

    [Fact]
    public async Task Publish_ExecutesOnlyAfterTheCSharpIndexAndConditionsMatch()
    {
        using var repository = CreateRepository(declareAppId: true);
        var executor = new RecordingExecutor();
        await using var service = new RepositoryAutomationTriggerService(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            queueCapacity: 8,
            maximumConcurrentRuns: 1,
            executor);
        await service.StartAsync();

        service.Publish(CreateEnvelope("another.app", "Error"));
        service.Publish(CreateEnvelope("com.example.app", "Information"));
        service.Publish(CreateEnvelope("com.example.app", "Error"));

        var request = await executor.FirstRequest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("capture-error", request.Trigger.TriggerId);
        Assert.Equal("Error", request.Event.Payload["priority"]?.GetValue<string>());
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task ConnectRepository_BindsVisibleModulesToTheSelectedHostAppId()
    {
        using var repository = CreateRepository(declareAppId: false);
        var executor = new RecordingExecutor();
        await using var service = new RepositoryAutomationTriggerService(
            [],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30),
            queueCapacity: 8,
            maximumConcurrentRuns: 1,
            executor);

        var inspection = service.InspectRepository(repository.Path, "com.example.app");
        Assert.True(inspection.IsSuccess);
        Assert.False(inspection.IsConnected);
        var inspectedTrigger = Assert.Single(inspection.Triggers);
        Assert.Equal("com.example.app", inspectedTrigger.AppId);
        Assert.Equal(
            GetTriggerPath(repository, "capture-error.ts"),
            inspectedTrigger.ModulePath);

        var connection = service.ConnectRepository(repository.Path, "com.example.app");
        Assert.True(connection.IsConnected);
        Assert.Single(service.RegisteredTriggers);

        await service.StartAsync();
        service.Publish(CreateEnvelope("com.example.app", "Error"));
        await executor.FirstRequest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, executor.ExecutionCount);

        Assert.True(service.DisconnectRepository("com.example.app"));
        Assert.Empty(service.RegisteredTriggers);

        Assert.True(service.ConnectRepository(repository.Path, "com.example.app").IsConnected);
        File.WriteAllText(GetTriggerPath(repository, "capture-error.ts"), "export default () => null;");
        var invalidReconnect = service.ConnectRepository(repository.Path, "com.example.app");
        Assert.False(invalidReconnect.IsConnected);
        Assert.Empty(service.RegisteredTriggers);
    }

    [Fact]
    public async Task JavaScriptExecutor_InvokesTheDefaultFunctionOnlyForAMatchedRequest()
    {
        using var repository = CreateRepository(declareAppId: true);
        var request = CreateMatchedRequest(repository, CreateEnvelope("com.example.app", "Error"));
        var appToolBridge = new RecordingAppToolBridge();
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Output?["action"]);
        Assert.Null(appToolBridge.ToolId);
    }

    [Fact]
    public async Task JavaScriptExecutor_RejectsAnUnavailableRuntimeWithoutRetryingAProcessStartFailure()
    {
        using var repository = CreateRepository(declareAppId: true);
        var request = CreateMatchedRequest(repository, CreateEnvelope("com.example.app", "Error"));
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "/missing/node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()),
            "Node.js runtime '/missing/node' is unavailable.");

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Rejected, result.Status);
        Assert.Contains("unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JavaScriptExecutor_ExecutesATypeScriptTriggerWithNativeTypeStripping()
    {
        using var repository = CreateTypeScriptRepository();
        var request = CreateMatchedRequest(repository, CreateEnvelope("com.example.app", "Error"));
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task JavaScriptExecutor_DispatchesAStandardReturnedAppToolWithCorrelation()
    {
        using var repository = CreateAppToolRepository();
        var envelope = CreateAppEventEnvelope("com.example.app");
        var request = CreateMatchedRequest(repository, envelope);
        var appToolBridge = new RecordingAppToolBridge();
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal("session-1", appToolBridge.SessionId);
        Assert.Equal("artifacts.request", appToolBridge.ToolId);
        Assert.Equal("example.diagnostics", appToolBridge.Arguments?["providerId"]?.GetValue<string>());
        Assert.Equal(envelope.CorrelationId, appToolBridge.RequestContext?.CorrelationId);
        Assert.Equal(["query", "call"], appToolBridge.Operations);
        Assert.Equal("artifacts.request", appToolBridge.QueryArguments?["toolId"]?.GetValue<string>());
        Assert.Equal("full", appToolBridge.QueryArguments?["detail"]?.GetValue<string>());
        Assert.False(appToolBridge.QueryArguments?["executableOnly"]?.GetValue<bool>());
        Assert.Equal(1, appToolBridge.ForwardedCallCount);
        Assert.Equal("artifacts.request", result.Output?["action"]?["toolId"]?.GetValue<string>());
        Assert.NotNull(result.Output?["result"]);
    }

    [Fact]
    public async Task JavaScriptExecutor_DoesNotDispatchWhenTheSessionCatalogCannotBeAuthenticated()
    {
        using var repository = CreateAppToolRepository();
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var appToolBridge = new RecordingAppToolBridge
        {
            QueryFailureMessage = "The authenticated catalog request was rejected."
        };
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Failed, result.Status);
        Assert.Equal("The authenticated catalog request was rejected.", result.Message);
        Assert.Equal(["query"], appToolBridge.Operations);
        Assert.Null(appToolBridge.ToolId);
        Assert.Equal(0, appToolBridge.ForwardedCallCount);
    }

    [Fact]
    public async Task JavaScriptExecutor_PreservesCatalogPolicyDenialWithoutDispatchingToTheApp()
    {
        using var repository = CreateAppToolRepository();
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var appToolBridge = new RecordingAppToolBridge
        {
            DenyToolCalls = true
        };
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Failed, result.Status);
        Assert.Contains("exceeds the authenticated client grant", result.Message, StringComparison.Ordinal);
        Assert.Equal(["query", "call"], appToolBridge.Operations);
        Assert.Equal("artifacts.request", appToolBridge.ToolId);
        Assert.Equal(0, appToolBridge.ForwardedCallCount);
    }

    [Fact]
    public async Task JavaScriptExecutor_DispatchesACustomAppToolWithoutDescriptorBoilerplate()
    {
        using var repository = CreateAppToolRepository(returnedToolId: "dangerous.tool");
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var appToolBridge = new RecordingAppToolBridge();
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal("dangerous.tool", appToolBridge.ToolId);
    }

    [Fact]
    public async Task JavaScriptExecutor_KillsASynchronouslyBlockedFunctionAtItsMillisecondBound()
    {
        using var repository = CreateRepository(
            declareAppId: true,
            functionTimeoutMs: 50,
            functionBody: "while (true) { }");
        var request = CreateMatchedRequest(repository, CreateEnvelope("com.example.app", "Error"));
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()));
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.TimedOut, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Contains("50-millisecond execution bound", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JavaScriptExecutor_StartsTheFunctionBoundAfterModuleLoading()
    {
        using var repository = CreateRepository(
            declareAppId: true,
            functionTimeoutMs: 50);
        var modulePath = GetTriggerPath(repository, "capture-error.ts");
        File.WriteAllText(
            modulePath,
            File.ReadAllText(modulePath).Replace(
                "export const trigger",
                "await new Promise(resolve => setTimeout(resolve, 250));\n\nexport const trigger",
                StringComparison.Ordinal));
        var request = CreateMatchedRequest(repository, CreateEnvelope("com.example.app", "Error"));
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()));
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task JavaScriptExecutor_GivesTheReturnedAppToolASeparateActionTimeout()
    {
        using var repository = CreateAppToolRepository(
            functionTimeoutMs: 500,
            actionTimeoutSeconds: 1,
            beforeReturn: "await new Promise(resolve => setTimeout(resolve, 250));");
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var appToolBridge = new RecordingAppToolBridge
        {
            CallDelay = TimeSpan.FromMilliseconds(800)
        };
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Assert.Equal("artifacts.request", appToolBridge.ToolId);
    }

    [Fact]
    public async Task JavaScriptExecutor_CancelsTheReturnedAppToolAtItsActionTimeout()
    {
        using var repository = CreateAppToolRepository(actionTimeoutSeconds: 1);
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var appToolBridge = new RecordingAppToolBridge
        {
            CallDelay = TimeSpan.FromSeconds(10)
        };
        var executor = new JavaScriptRepositoryAutomationExecutor(
            "node",
            new AppToolRepositoryAutomationExecutor(appToolBridge));
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(AutomationRunStatus.TimedOut, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Contains("1-second timeout", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAppEvent_PreservesTheRawEventIdentityAndFields()
    {
        var runtimeEvent = new RuntimeAppEvent(
            DateTimeOffset.Parse("2026-08-13T01:02:03Z"),
            "raw-app-event-42",
            "session-1",
            "com.example.app",
            "Redpoint",
            "MapPage",
            "Info",
            "Map navigation settled.",
            4);

        var normalized = AutomationEventNormalizer.TryNormalize(runtimeEvent, out var envelope);

        Assert.True(normalized);
        Assert.NotNull(envelope);
        Assert.Equal(AutomationEventNormalizer.AppEventKind, envelope.Kind);
        Assert.Equal(runtimeEvent.EventId, envelope.EventId);
        Assert.Equal("MapPage", envelope.Payload["label"]?.GetValue<string>());
        Assert.Equal("Map navigation settled.", envelope.Payload["details"]?.GetValue<string>());
        Assert.Equal((byte)4, envelope.Payload["channelId"]?.GetValue<byte>());
    }

    [Fact]
    public void NormalizeTrendsEvent_ExposesRegressionToRepositoryTriggers()
    {
        var runtimeEvent = new RuntimeTrendsEvent(
            DateTimeOffset.Parse("2026-08-20T01:02:03Z"),
            RuntimeTrendsEventKind.RegressionDetected,
            "session-1",
            "com.example.app",
            "guide-memory-regression",
            "Retained memory regressed.",
            HistoryStatus: WorkspaceTrendsHistoryStatus.Regressed);

        var normalized = AutomationEventNormalizer.TryNormalize(runtimeEvent, out var envelope);

        Assert.True(normalized);
        Assert.NotNull(envelope);
        Assert.Equal("trends.regression.detected", envelope.Kind);
        Assert.Equal("guide-memory-regression", envelope.Payload["definitionId"]?.GetValue<string>());
        Assert.Equal("Regressed", envelope.Payload["historyStatus"]?.GetValue<string>());
    }

    private static TestDirectory CreateRepository(
        bool declareAppId,
        int functionTimeoutMs = 100,
        int actionTimeoutSeconds = 10,
        string functionBody = "return null;",
        string retryDescriptor = "")
    {
        var repository = TestDirectory.Create();
        var triggerDirectory = Path.Combine(repository.Path, "ansight", "triggers");
        Directory.CreateDirectory(triggerDirectory);
        var appIdLine = declareAppId ? "\"appId\": \"com.example.app\"," : string.Empty;
        File.WriteAllText(
            Path.Combine(triggerDirectory, "capture-error.ts"),
            $$"""
            export const trigger = {
              "schemaVersion": 1,
              {{appIdLine}}
              "eventKind": "session.log.received",
              "eventSchema": {
                "type": "object",
                "properties": {
                  "priority": { "type": "string" }
                },
                "required": ["priority"],
                "additionalProperties": true
              },
              "functionTimeoutMs": {{functionTimeoutMs}},
              "actionTimeoutSeconds": {{actionTimeoutSeconds}}{{retryDescriptor}},
              "conditions": [
                {
                  "field": "payload.priority",
                  "operator": "equals",
                  "value": "Error"
                }
              ]
            };

            export default async function captureError({ event }) {
              {{functionBody}}
            }
            """);
        return repository;
    }

    private static TestDirectory CreateAppToolRepository(
        string returnedToolId = "artifacts.request",
        int functionTimeoutMs = 100,
        int actionTimeoutSeconds = 10,
        string beforeReturn = "")
    {
        var repository = TestDirectory.Create();
        var triggerDirectory = Path.Combine(repository.Path, "ansight", "triggers");
        Directory.CreateDirectory(triggerDirectory);
        var source = $$"""
            export const trigger = {
              "appId": "com.example.app",
              "eventKind": "app.event",
              "functionTimeoutMs": {{functionTimeoutMs}},
              "actionTimeoutSeconds": {{actionTimeoutSeconds}},
              "conditions": [
                {
                  "field": "payload.label",
                  "operator": "equals",
                  "value": "ExampleService"
                }
              ]
            };

            export default async function captureAppState({ app }) {
              {{beforeReturn}}
              return app.callTool("{{returnedToolId}}", {
                providerId: "example.diagnostics",
                artifactId: "state"
              });
            }
            """;
        if (string.Equals(returnedToolId, "artifacts.request", StringComparison.Ordinal))
        {
            source = source.Replace(
                "app.callTool(\"artifacts.request\",",
                "app.artifacts.request(",
                StringComparison.Ordinal);
        }

        File.WriteAllText(
            Path.Combine(triggerDirectory, "capture-app-state.ts"),
            source);
        return repository;
    }

    private static TestDirectory CreateTypeScriptRepository()
    {
        var repository = TestDirectory.Create();
        var triggerDirectory = Path.Combine(repository.Path, "ansight", "triggers");
        Directory.CreateDirectory(triggerDirectory);
        File.WriteAllText(
            Path.Combine(triggerDirectory, "capture-typed-error.ts"),
            """
            export const trigger = {
              "appId": "com.example.app",
              "eventKind": "session.log.received",
              "functionTimeoutMs": 100,
              "actionTimeoutSeconds": 10,
              "conditions": [
                {
                  "field": "payload.priority",
                  "operator": "equals",
                  "value": "Error"
                }
              ]
            };

            type Invocation = {
              event: { eventId: string };
            };

            export default async function captureTypedError({ event }: Invocation) {
              return event.eventId.length > 0 ? null : null;
            }
            """);
        return repository;
    }

    private static RepositoryAutomationExecutionRequest CreateMatchedRequest(
        TestDirectory repository,
        AutomationEventEnvelope envelope)
    {
        var loadResult = RepositoryAutomationTriggerLoader.Load(
            [repository.Path],
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(30));
        var trigger = Assert.Single(loadResult.Catalog.FindMatches(envelope));
        return new RepositoryAutomationExecutionRequest(
            "run-1",
            trigger,
            envelope,
            DateTimeOffset.UtcNow);
    }

    private static string GetTriggerPath(TestDirectory repository, string fileName)
        => Path.Combine(repository.Path, "ansight", "triggers", fileName);

    private static AutomationEventEnvelope CreateEnvelope(string appId, string priority)
    {
        return new AutomationEventEnvelope
        {
            EventId = Guid.CreateVersion7().ToString("N"),
            Kind = AutomationEventNormalizer.SessionLogReceivedKind,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            AppId = appId,
            SessionId = "session-1",
            CorrelationId = Guid.CreateVersion7().ToString("N"),
            Payload = new JsonObject
            {
                ["priority"] = priority
            }
        };
    }

    private static AutomationEventEnvelope CreateAppEventEnvelope(string appId)
    {
        return new AutomationEventEnvelope
        {
            EventId = Guid.CreateVersion7().ToString("N"),
            Kind = AutomationEventNormalizer.AppEventKind,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            AppId = appId,
            SessionId = "session-1",
            CorrelationId = Guid.CreateVersion7().ToString("N"),
            Payload = new JsonObject
            {
                ["label"] = "ExampleService"
            }
        };
    }

    private sealed class RecordingExecutor : IRepositoryAutomationExecutor
    {
        private readonly TaskCompletionSource<RepositoryAutomationExecutionRequest> firstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int executionCount;

        public Task<RepositoryAutomationExecutionRequest> FirstRequest => firstRequest.Task;

        public int ExecutionCount => Volatile.Read(ref executionCount);

        public Task<RepositoryAutomationExecutionResult> ExecuteAsync(
            RepositoryAutomationExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref executionCount);
            firstRequest.TrySetResult(request);
            var nowUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(new RepositoryAutomationExecutionResult(
                AutomationRunStatus.Succeeded,
                nowUtc,
                nowUtc,
                ExitCode: 0,
                "Completed.",
                new JsonObject(),
                StandardError: string.Empty));
        }
    }

    private sealed class SequencedExecutor(params AutomationRunStatus[] statuses) : IRepositoryAutomationExecutor
    {
        private int executionCount;

        public Task<RepositoryAutomationExecutionResult> ExecuteAsync(
            RepositoryAutomationExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref executionCount) - 1, statuses.Length - 1);
            var nowUtc = DateTimeOffset.UtcNow;
            return Task.FromResult(new RepositoryAutomationExecutionResult(
                statuses[index],
                nowUtc,
                nowUtc,
                ExitCode: statuses[index] == AutomationRunStatus.Succeeded ? 0 : 1,
                $"Attempt {request.AttemptNumber} {statuses[index]}.",
                new JsonObject { ["attempt"] = request.AttemptNumber },
                StandardError: statuses[index] == AutomationRunStatus.Succeeded ? string.Empty : "failed"));
        }
    }

    private sealed class RecordingAppToolBridge : IAppToolBridge
    {
        private bool catalogQueried;

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public string? SessionId { get; private set; }

        public string? ToolId { get; private set; }

        public JsonObject? Arguments { get; private set; }

        public AppToolBridgeRequestContext? RequestContext { get; private set; }

        public JsonObject? QueryArguments { get; private set; }

        public List<string> Operations { get; } = [];

        public int ForwardedCallCount { get; private set; }

        public TimeSpan CallDelay { get; init; }

        public string? QueryFailureMessage { get; init; }

        public bool DenyToolCalls { get; init; }

        public IReadOnlyList<string> GetConnectedSessionIds() => ["session-1"];

        public bool IsSessionConnected(string sessionId) =>
            string.Equals(sessionId, "session-1", StringComparison.Ordinal);

        public OperationResult ForceDisconnectSession(string sessionId) =>
            OperationResult.Success("Disconnected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            Operations.Add("query");
            if (QueryFailureMessage is not null)
            {
                return Task.FromResult(AppToolBridgeResponse.FromFailure(QueryFailureMessage));
            }

            catalogQueried = true;
            return Task.FromResult(AppToolBridgeResponse.FromSuccess(
                "Catalog loaded.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.CatalogType,
                    Id = "catalog-response-1",
                    SessionId = sessionId,
                    Payload = new JsonObject
                    {
                        ["tools"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["id"] = "artifacts.request",
                                ["policy"] = "read"
                            },
                            new JsonObject
                            {
                                ["id"] = "dangerous.tool",
                                ["policy"] = "read"
                            }
                        }
                    }
                }));
        }

        public Task<AppToolBridgeResponse> QueryToolsFilteredAsync(
            string sessionId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            QueryArguments = arguments?.DeepClone().AsObject();
            return QueryToolsAsync(sessionId, cancellationToken, requestContext);
        }

        public async Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            SessionId = sessionId;
            ToolId = toolId;
            Arguments = arguments;
            RequestContext = requestContext;
            Operations.Add("call");
            if (!catalogQueried)
            {
                return AppToolBridgeResponse.FromFailure(
                    $"Query the authenticated tool catalog before invoking tool '{toolId}'.",
                    requiresCatalogQuery: true);
            }

            if (DenyToolCalls)
            {
                return AppToolBridgeResponse.FromFailure(
                    "Tool policy 'critical' exceeds the authenticated client grant 'read'.");
            }

            ForwardedCallCount++;
            if (CallDelay > TimeSpan.Zero)
            {
                await Task.Delay(CallDelay, cancellationToken);
            }

            return AppToolBridgeResponse.FromSuccess(
                "Completed.",
                new ToolProtocolEnvelope
                {
                    Type = ToolProtocolMessageTypes.ResultType,
                    Id = "response-1",
                    SessionId = sessionId,
                    Payload = new JsonObject
                    {
                        ["result"] = new JsonObject
                        {
                            ["status"] = "complete"
                        }
                    }
                });
        }
    }
}
