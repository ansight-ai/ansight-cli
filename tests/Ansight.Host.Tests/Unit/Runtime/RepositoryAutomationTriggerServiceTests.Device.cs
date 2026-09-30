using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RepositoryAutomationTriggerServiceTests
{
    [Theory]
    [InlineData(SessionLogStreamIds.AndroidLogcat)]
    [InlineData(SessionLogStreamIds.AppleUnifiedLog)]
    public async Task NativeLogTriggerMatchesAndCapturesEvidenceWithoutSdk(string streamId)
    {
        using var repository = CreateAppToolRepository();
        File.WriteAllText(GetTriggerPath(repository, "capture-app-state.ts"), $$"""
            export const trigger = {
              "schemaVersion": 2, "appId": "com.example.app", "eventKind": "session.log.received",
              "requires": { "capabilities": ["files.capture"] },
              "conditions": [
                { "field": "payload.streamId", "operator": "equals", "value": "{{streamId}}" },
                { "field": "payload.priority", "operator": "equals", "value": "Error" },
                { "field": "payload.tag", "operator": "equals", "value": "Notes" }
              ]
            };
            export default ({ ansight, event, app }) => {
              if (app.available || event.payload.processId !== 42) throw new Error("Wrong log context");
              return ansight.files.capture({ path: "Documents/notes.sqlite" });
            };
            """);
        var entry = new LogEntry(DateTimeOffset.UtcNow, "Failed to save note")
        { Priority = LogPriority.Error, Tag = "Notes", Source = "Native", ProcessId = 42, ThreadId = 43 };
        var batch = new SessionLogBatchEventArgs("session-1", "com.example.app", streamId, [entry], 1, 1);
        var request = CreateMatchedRequest(repository, AutomationEventNormalizer.NormalizeLog(batch, entry));
        var calls = new List<string>();
        var executor = new JavaScriptRepositoryAutomationExecutor("node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()), hostTools: (name, arguments, _) =>
            {
                calls.Add(name);
                Assert.Equal("session-1", arguments["sessionId"]?.GetValue<string>());
                return Task.FromResult(RequestResult.ToolResult(name == "ansight_get_execution_capabilities"
                    ? new JsonObject { ["executionMode"] = "device", ["appAvailable"] = false,
                        ["capabilities"] = new JsonObject { ["files.capture"] = new JsonObject { ["available"] = true } } }
                    : new JsonObject { ["snapshotId"] = "captured" }, false));
            });
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "ansight_get_execution_capabilities", "ansight_capture_sandbox_file" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceTriggerPreflightsAndPinsHostEvidence(bool unavailable)
    {
        using var repository = CreateAppToolRepository();
        File.WriteAllText(GetTriggerPath(repository, "capture-app-state.ts"), """
            export const trigger = {
              "schemaVersion": 2, "appId": "com.example.app", "eventKind": "app.event",
              "requires": { "capabilities": ["files.capture"] }
            };
            export default ({ ansight, app, run }) => {
              if (app.available || run.executionMode !== "device") throw new Error("Wrong capabilities");
              return ansight.files.capture({ path: "Documents/probe.bin", sessionId: "wrong", appId: "wrong" });
            };
            """);
        var request = CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app"));
        var calls = new List<string>();
        var executor = new JavaScriptRepositoryAutomationExecutor("node",
            new AppToolRepositoryAutomationExecutor(new RecordingAppToolBridge()), hostTools: (name, arguments, _) =>
            {
                calls.Add(name);
                Assert.Equal("session-1", arguments["sessionId"]?.GetValue<string>());
                Assert.Null(arguments["appId"]);
                return Task.FromResult(RequestResult.ToolResult(name == "ansight_get_execution_capabilities"
                    ? new JsonObject
                    {
                        ["executionMode"] = "device", ["appAvailable"] = false,
                        ["capabilities"] = new JsonObject { ["files.capture"] = new JsonObject { ["available"] = !unavailable } }
                    }
                    : new JsonObject { ["snapshotId"] = "captured" }, false));
            });
        var result = await executor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(unavailable ? AutomationRunStatus.Rejected : AutomationRunStatus.Succeeded, result.Status);
        Assert.Equal(unavailable ? 1 : 2, calls.Count);
        if (!unavailable) Assert.Equal("ansight_capture_sandbox_file", calls[1]);
    }

    [Fact]
    public async Task DeviceTriggerDynamicAppCallIsRejectedWithoutDispatch()
    {
        using var repository = CreateAppToolRepository();
        var path = GetTriggerPath(repository, "capture-app-state.ts");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"appId\":", "\"schemaVersion\": 2, \"appId\":"));
        var bridge = new RecordingAppToolBridge();
        var executor = new JavaScriptRepositoryAutomationExecutor("node", new AppToolRepositoryAutomationExecutor(bridge),
            hostTools: (_, _, _) => Task.FromResult(RequestResult.ToolResult(
                new JsonObject { ["executionMode"] = "device", ["appAvailable"] = false }, false)));
        var result = await executor.ExecuteAsync(CreateMatchedRequest(repository, CreateAppEventEnvelope("com.example.app")), CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Rejected, result.Status);
        Assert.Empty(bridge.Operations);
    }
}
