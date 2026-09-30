using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsync_StopsAfterFailedReplayInputWhenContinuationIsDisabled()
    {
        const string failure = "Simulator HID delivery failed: Mach port invalid, device disconnected";
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("tap-first", "ansight_tap_ui", new JsonObject
            {
                ["normalizedX"] = 0.4,
                ["normalizedY"] = 0.9
            }),
            CreateFunctionTurn("complete-first", "complete_instruction", new JsonObject
            {
                ["outcome"] = "failed",
                ["summary"] = failure
            }),
            CreateFunctionTurn("tap-second", "ansight_tap_ui", new JsonObject
            {
                ["normalizedX"] = 0.3,
                ["normalizedY"] = 0.1
            })
        ]);
        var gateway = new FakeToolGateway
        {
            Result = new ToolCallResult(true, "{\"isError\":true}", failure)
        };
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Replay the first tap.", "Replay the second tap."],
            ContinueAfterInstructionFailure: false));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(SimulatorAgentInstructionStatus.Failed, Assert.Single(result.Instructions).Status);
        Assert.Equal(failure, result.Instructions[0].Summary);
        Assert.Single(gateway.Calls);
        Assert.Equal(2, client.Inputs.Count);
        Assert.Contains(failure, result.Message, StringComparison.Ordinal);
        Assert.False(result.Audit.ContinueAfterInstructionFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RespectsContinuationPolicyAfterObservationStagnation(bool continueAfterFailure)
    {
        var observations = Enumerable.Range(1, 9).Select(index => CreateFunctionTurn(
            $"observe-{index}", "ansight_get_live_visual_tree",
            new JsonObject { ["maxNodes"] = 100 + index }));
        var client = new FakeOpenAiClient(observations.Append(CreateFunctionTurn(
            "complete-second", "complete_instruction", new JsonObject
            {
                ["outcome"] = "succeeded",
                ["summary"] = "The second instruction completed."
            })));
        var gateway = new FakeToolGateway();
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123",
            ["Select the exact map annotation.", "Check the next state."],
            MaximumTurnsPerInstruction: 12,
            ContinueAfterInstructionFailure: continueAfterFailure));

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(continueAfterFailure ? 2 : 1, result.Instructions.Count);
        Assert.Equal(continueAfterFailure ? 10 : 9, client.Inputs.Count);
        Assert.Equal(8, gateway.Calls.Count);
        Assert.Contains("8 consecutive read-only", result.Instructions[0].Summary, StringComparison.Ordinal);
        Assert.Equal(continueAfterFailure, result.Audit.ContinueAfterInstructionFailure);
        if (continueAfterFailure)
        {
            Assert.Equal(SimulatorAgentInstructionStatus.Succeeded, result.Instructions[1].Status);
        }
    }
}
