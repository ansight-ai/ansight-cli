using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskResults_PreserveIndividualPayloadsAndTimingIncludingAfterCancellation(bool cancelDuringCall)
    {
        using var cancellation = new CancellationTokenSource();
        var started = DateTimeOffset.Parse("2026-09-07T03:00:00Z");
        var input = new RepositoryTaskCallPayload("{\"selector\":\"account\"}", 22, false, "input-hash");
        var output = new RepositoryTaskCallPayload("{\"visible\":true}", 16, false, "output-hash");
        var assertion = new RepositoryTaskAssertion("visible", true, "Account is visible", JsonValue.Create(true), JsonValue.Create(true))
        { Matcher = "expect.toBe", CompletedAtUtc = started };
        var child = new RepositoryTaskToolCall(1, "ansight_wait_for_ui", started, 123, false, "Found account.")
        {
            Arguments = input,
            Result = output,
            CompletedAtUtc = started.AddMilliseconds(123),
            CorrelationId = "task:1"
        };
        var parent = new RepositoryTaskToolCall(1, "ansight.tasks.run", started, 125, false, "Child passed.")
        {
            ChildCalls = [child]
        };
        var source = new RepositoryTaskSourceTrace("open-account",
            [new RepositoryTaskSourceModule("ansight/tasks/account.ts", "typescript", "// captured", "hash", 11, false)]);
        var dispatcher = new RecordingOperationDispatcher
        {
            ResponseFactory = (_, _) =>
            {
                if (cancelDuringCall)
                {
                    cancellation.Cancel();
                }
                return RequestResult.ToolResult(new JsonObject
                {
                    ["sourceTrace"] = JsonSerializer.SerializeToNode(source, JsonUtil.Compact),
                    ["taskId"] = "open-account",
                    ["status"] = "Passed",
                    ["message"] = "Task passed.",
                    ["assertions"] = JsonSerializer.SerializeToNode(new[] { assertion }, JsonUtil.Compact),
                    ["toolCalls"] = JsonSerializer.SerializeToNode(new[] { parent }, JsonUtil.Compact)
                }, isError: false);
            }
        };
        var gateway = new ToolGateway(dispatcher);

        var result = await gateway.ExecuteAsync("ansight_run_task", new JsonObject { ["taskId"] = "open-account" },
            "session", "correlation", cancellation.Token);

        var retained = Assert.Single(Assert.Single(result.TaskCalls!).ChildCalls!);
        Assert.Equal(source.TaskId, result.TaskSource!.TaskId);
        Assert.Equal(source.Modules[0], Assert.Single(result.TaskSource.Modules));
        Assert.DoesNotContain("sourceTrace", result.ModelOutput!);
        Assert.DoesNotContain("// captured", result.Output);
        Assert.Equal(child, retained);
        var retainedAssertion = Assert.Single(result.TaskAssertions!);
        Assert.True(retainedAssertion.Actual!.GetValue<bool>());
        Assert.Equal("expect.toBe", retainedAssertion.Matcher);
        Assert.Null(JsonNode.Parse(result.ModelOutput!)!["result"]!["assertions"]![0]!["actual"]);
        Assert.Equal(input, retained.Arguments);
        Assert.Equal(output, retained.Result);
        Assert.Null(JsonNode.Parse(result.ModelOutput!)!["result"]!["toolCalls"]);
    }
}
