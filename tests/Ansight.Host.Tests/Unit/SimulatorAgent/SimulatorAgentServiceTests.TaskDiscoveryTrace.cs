using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ReportsAndPersistsTaskDiscoveryOnlyWithTrace(bool captureTrace)
    {
        var trace = new SimulatorAgentRepositoryTaskDiscoveryTrace("query", "Open weather", [])
        {
            AvailableTaskCount = 1,
            AvailableTaskIds = ["load-weather"]
        };
        var gateway = new FakeToolGateway { RepositoryTaskDiscoveryTraces = [trace] };
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("complete", "complete_instruction", new JsonObject
            {
                ["outcome"] = "failed",
                ["summary"] = "No applicable action was performed."
            })
        ]);
        var reported = new List<SimulatorAgentProgress>();
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(
            new SimulatorAgentRunRequest("session-123", ["Open weather"]) { CaptureTrace = captureTrace },
            new SynchronousProgress<SimulatorAgentProgress>(reported.Add));

        if (captureTrace)
        {
            var saved = Assert.Single(result.Audit.RepositoryTaskDiscovery);
            Assert.Equal(1, saved.InstructionIndex);
            Assert.Equal(trace.Query, saved.Query);
            Assert.Equal(trace.AvailableTaskIds, saved.AvailableTaskIds);
            var progress = Assert.Single(reported, item => item.Stage == SimulatorAgentProgressStage.TaskDiscovery);
            Assert.Equal(saved, progress.TaskDiscovery);
        }
        else
        {
            Assert.Empty(result.Audit.RepositoryTaskDiscovery);
            Assert.DoesNotContain(reported, item => item.Stage == SimulatorAgentProgressStage.TaskDiscovery);
        }
    }
}
