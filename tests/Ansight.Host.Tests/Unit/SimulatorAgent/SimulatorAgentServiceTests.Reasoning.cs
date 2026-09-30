using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(SimulatorAgentOpenAiProtocol.Http)]
    [InlineData(SimulatorAgentOpenAiProtocol.WebSocket)]
    public async Task RunAsync_UsesResolvedReasoningSettingsAndRecordsTheirRevision(
        SimulatorAgentOpenAiProtocol protocol)
    {
        var turns = new[]
        {
            CreateFunctionTurn("observe", "ansight_get_live_visual_tree", new JsonObject { ["maxNodes"] = 100 }),
            CreateFunctionTurn("complete", "complete_instruction", new JsonObject
            {
                ["outcome"] = "succeeded",
                ["summary"] = "The welcome screen is visible."
            })
        };
        var client = new FakeOpenAiClient(turns);
        var session = new FakeOpenAiSession(turns);
        using var service = new SimulatorAgentService(
            new InMemoryEncryptedStorage(), client, new FakeToolGateway(),
            openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(session));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest(
            "session-123", ["Verify the welcome screen."], Model: "gpt-5.6-sol")
        {
            OpenAiProtocol = protocol,
            Reasoning = AgentReasoningModes.Deep,
            ReasoningEffort = "high",
            ReasoningConfigurationRevision = "organisation-override-7"
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var requests = protocol == SimulatorAgentOpenAiProtocol.Http ? client.Requests : session.Requests;
        Assert.NotEmpty(requests);
        Assert.All(requests, request =>
        {
            Assert.Equal("gpt-5.6-sol", request.Model);
            Assert.Equal("high", request.ReasoningEffort);
        });
        Assert.Equal("deep", result.Audit.Reasoning);
        Assert.Equal("gpt-5.6-sol", result.Audit.Model);
        Assert.Equal("high", result.Audit.ReasoningEffort);
        Assert.Equal("organisation-override-7", result.Audit.ReasoningConfigurationRevision);
    }
}
