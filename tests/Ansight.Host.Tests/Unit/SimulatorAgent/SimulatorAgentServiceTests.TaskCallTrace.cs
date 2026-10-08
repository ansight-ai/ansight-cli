using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RetainsTaskCallPayloadsOutsideParentResultLimitOnlyWhenTraced(bool captureTrace)
    {
        var arguments = new RepositoryTaskCallPayload("{\"automationId\":\"account\"}", 26, false, "input-hash");
        var output = new RepositoryTaskCallPayload("{\"visible\":true}", 16, false, "output-hash");
        var assertion = new RepositoryTaskAssertion("visible", true, "Account is visible", JsonValue.Create(true), JsonValue.Create(true))
        { Matcher = "expect.toBe" };
        var taskCall = new RepositoryTaskToolCall(1, "ansight_wait_for_ui", DateTimeOffset.UtcNow, 123, false, "Found account.")
        {
            Arguments = arguments,
            Result = output,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            CorrelationId = "task:1"
        };
        var source = new RepositoryTaskSourceTrace("account",
            [new RepositoryTaskSourceModule("ansight/tasks/account.ts", "typescript", "// original code", "hash", 16, false)]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_account", "account", "Open account")],
            Result = new ToolCallResult(false, new string('x', 300_000), "Task passed.")
            {
                ModelOutput = "{\"status\":\"Passed\"}",
                TaskCalls = [taskCall],
                TaskAssertions = [assertion],
                TaskSource = source
            }
        };
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("task", "ansight_task_account", new JsonObject()), CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        Assert.False(RepositoryTaskTraceScope.IsEnabled);
        var result = await service.RunAsync(ToolLoadingRequest("Open account.") with { CaptureTrace = captureTrace });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(captureTrace, Assert.Single(gateway.TaskTraceStates));
        Assert.False(RepositoryTaskTraceScope.IsEnabled);
        var call = Assert.Single(result.Audit.ToolCalls, item => item.CallId == "task");
        if (captureTrace)
        {
            Assert.True(call.Result.WasTruncated);
            Assert.Equal(source, call.TaskSource);
            Assert.Equal(taskCall, Assert.Single(call.TaskCalls!));
            Assert.Equal(assertion, Assert.Single(call.TaskAssertions!));
            var persisted = JsonSerializer.Deserialize<SimulatorAgentRunAudit>(
                JsonSerializer.Serialize(result.Audit, JsonUtil.Compact), JsonUtil.Compact)!;
            var restored = Assert.Single(Assert.Single(persisted.ToolCalls, item => item.CallId == "task").TaskCalls!);
            Assert.Equal(source.Modules[0], Assert.Single(persisted.ToolCalls.Single(item => item.CallId == "task").TaskSource!.Modules));
            Assert.Equal(arguments, restored.Arguments);
            Assert.Equal(output, restored.Result);
            Assert.Equal(taskCall.StartedAtUtc, restored.StartedAtUtc);
            Assert.Equal(taskCall.CompletedAtUtc, restored.CompletedAtUtc);
            Assert.Equal("task:1", restored.CorrelationId);
            var restoredAssertion = Assert.Single(persisted.ToolCalls.Single(item => item.CallId == "task").TaskAssertions!);
            Assert.True(restoredAssertion.Actual!.GetValue<bool>());
            Assert.Equal("expect.toBe", restoredAssertion.Matcher);
        }
        else
        {
            Assert.Null(call.TaskCalls);
            Assert.Null(call.TaskSource);
            Assert.Null(call.TaskAssertions);
            Assert.Empty(call.Result.Content);
        }
    }
}
