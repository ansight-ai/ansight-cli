using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData("unknown", true)]
    [InlineData("foreground", true)]
    [InlineData("background", false)]
    public async Task RunAsync_MissingLifecycleEventDoesNotDeferInitialObservationToTheModel(string appState, bool capture)
    {
        var gateway = new FakeToolGateway
        {
            AppState = appState,
            InitialObservation = new ToolCallResult(false, "Current main tabs are ready", "Initial capture")
        };
        var session = new FakeOpenAiSession([CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Run the known navigation task."));

        Assert.Equal(capture, Assert.Single(session.Requests).Input.ToJsonString().Contains("Current main tabs are ready"));
        Assert.Equal(capture ? 1 : 0, result.Audit.ToolCalls.Count(call => call.InstructionTurn == 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_InitialObservationIsInFirstPayloadAndOnlyAuditKeepsFullResult(bool failed)
    {
        var gateway = new FakeToolGateway
        {
            InitialObservation = new ToolCallResult(failed, "full-host-evidence", "Initial capture")
            {
                ModelOutput = failed ? "{\"isError\":true,\"message\":\"Capture unavailable\"}"
                    : "{\"isError\":false,\"result\":{\"root\":{\"automationId\":\"HomePage\"}}}"
            }
        };
        var session = new FakeOpenAiSession([CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), new FakeOpenAiClient([]),
            gateway, openAiWebSocketSessionFactory: new FakeOpenAiSessionFactory(session));
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Verify the current page."])
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.WebSocket,
            CaptureTrace = true
        });
        var input = Assert.Single(session.Requests).Input.ToJsonString();
        Assert.Contains("Host-provided current-page observation", input);
        Assert.Contains(failed ? "Capture unavailable" : "HomePage", input);
        Assert.DoesNotContain("full-host-evidence", input);
        Assert.Empty(gateway.Calls);

        var capture = Assert.Single(result.Audit.ToolCalls, call => call.InstructionTurn == 0);
        Assert.Equal("full-host-evidence", capture.Result.Content);
        Assert.Equal(failed, capture.IsError);
    }
}
