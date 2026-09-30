using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsync_DoesNotSelectNavigationInstructionsFromRepositoryTaskOutput()
    {
        var gateway = new FakeToolGateway { Result = NavigationObservation("maui") };
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("task", "ansight_run_task", new JsonObject { ["taskId"] = "inspect-domain" }),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Run the inspection task."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Single(gateway.Calls);
        Assert.All(session.Requests, request => Assert.Empty(NavigationGuidanceInputs(request.Input)));
    }

    [Fact]
    public async Task RunAsync_SendsObservedNavigationGuidanceOnceAndRetainsItAcrossCheckpoints()
    {
        var gateway = new FakeToolGateway { InitialObservation = NavigationObservation("maui") };
        gateway.Results.Enqueue(NavigationObservation("maui"));
        gateway.Results.Enqueue(NavigationObservation("react-native"));
        gateway.Results.Enqueue(NavigationObservation("react-native"));
        AddToolLoadingDefinitions(gateway);
        var checkpoint = new JsonObject
        {
            ["type"] = "compaction", ["id"] = "navigation-checkpoint", ["encrypted_content"] = "retained-state"
        };
        var afterCheckpoint = CreateFunctionTurn("second-tree", "ansight_get_live_visual_tree", new JsonObject());
        afterCheckpoint.Output.Insert(0, checkpoint.DeepClone());
        var session = new FakeOpenAiSession([
            CreateFunctionTurn("first-tree", "ansight_get_live_visual_tree", new JsonObject()),
            afterCheckpoint,
            CreateFunctionTurn("third-tree", "ansight_get_live_visual_tree", new JsonObject()),
            CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect the current page and navigation."));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(NavigationGuidance.Read("maui"),
            Assert.Single(NavigationGuidanceInputs(session.Requests[0].IncrementalInput!)), StringComparison.Ordinal);
        Assert.Empty(NavigationGuidanceInputs(session.Requests[1].IncrementalInput!));
        Assert.Contains(NavigationGuidance.Read("react-native"),
            Assert.Single(NavigationGuidanceInputs(session.Requests[2].IncrementalInput!)), StringComparison.Ordinal);
        Assert.Empty(NavigationGuidanceInputs(session.Requests[3].IncrementalInput!));
        foreach (var request in session.Requests.Skip(2))
        {
            Assert.Equal(2, NavigationGuidanceInputs(request.Input).Count());
            Assert.Single(request.Input.OfType<JsonObject>(), item => JsonNode.DeepEquals(item, checkpoint));
            Assert.False(request.StartNewConversation);
        }
    }

    [Fact]
    public async Task RunAsync_RestoresNavigationGuidanceAtEachNewInstructionConversation()
    {
        var gateway = new FakeToolGateway { InitialObservation = NavigationObservation("maui") };
        AddToolLoadingDefinitions(gateway);
        var session = new FakeOpenAiSession([CompleteWebSocketTurn(), CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Inspect account.") with
        {
            Instructions = ["Inspect account.", "Inspect settings."]
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, session.Requests.Count);
        Assert.All(session.Requests, request =>
        {
            Assert.True(request.StartNewConversation);
            Assert.Single(NavigationGuidanceInputs(request.Input));
            Assert.Single(NavigationGuidanceInputs(request.IncrementalInput!));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_HttpFallbackPreservesEarlyAndLateNavigationGuidanceWhenTrimming(bool withCheckpoint)
    {
        const int actionCount = 65;
        var gateway = new FakeToolGateway
        {
            InitialObservation = NavigationObservation("maui"),
            Result = NavigationObservation("react-native")
        };
        for (var index = 0; index < actionCount - 1; index++)
        {
            gateway.Results.Enqueue(NavigationObservation("maui"));
        }
        AddToolLoadingDefinitions(gateway, "ansight_tap_ui");
        var actions = Enumerable.Range(0, actionCount).Select(index =>
            CreateFunctionTurn($"tap-{index}", "ansight_tap_ui", new JsonObject { ["automationId"] = $"control-{index}" }))
            .ToArray();
        if (withCheckpoint)
        {
            actions[0].Output.Insert(0, new JsonObject
            {
                ["type"] = "compaction", ["id"] = "before-navigation-change", ["encrypted_content"] = "retained-state"
            });
        }
        var session = new FakeOpenAiSession(new[] { LoadManualToolsTurn("load") }.Concat(actions));
        var httpClient = new FakeOpenAiClient([CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(
            new FailAfterToolLoadingSession(session, actionCount + 1), gateway, httpClient);

        var result = await service.RunAsync(ToolLoadingRequest("Tap the requested controls.") with
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Auto,
            MaximumTurnsPerInstruction = 80,
            MaximumRoundTrips = 80
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var replay = Assert.Single(httpClient.Inputs);
        Assert.True(replay.Count <= 120);
        var guidance = NavigationGuidanceInputs(replay).ToArray();
        Assert.Equal(2, guidance.Length);
        Assert.Single(guidance, text => text.Contains(NavigationGuidance.Read("maui"), StringComparison.Ordinal));
        Assert.Single(guidance, text => text.Contains(NavigationGuidance.Read("react-native"), StringComparison.Ordinal));
        Assert.Equal(withCheckpoint ? 1 : 0, replay.OfType<JsonObject>().Count(item => item["type"]?.GetValue<string>() == "compaction"));
    }

    [Fact]
    public async Task RunAsync_DirectHttpHistoryProtectsNavigationGuidanceFromLongToolTails()
    {
        const int actionCount = 65;
        var gateway = new FakeToolGateway { InitialObservation = NavigationObservation("maui") };
        AddToolLoadingDefinitions(gateway, "ansight_tap_ui");
        var client = new FakeOpenAiClient(Enumerable.Range(0, actionCount).Select(index =>
            CreateFunctionTurn($"tap-{index}", "ansight_tap_ui", new JsonObject { ["automationId"] = $"control-{index}" }))
            .Append(CompleteWebSocketTurn()));
        using var service = CreateToolLoadingService(new FakeOpenAiSession([]), gateway, client);

        var result = await service.RunAsync(ToolLoadingRequest("Tap the requested controls.") with
        {
            OpenAiProtocol = SimulatorAgentOpenAiProtocol.Http,
            MaximumTurnsPerInstruction = 80,
            MaximumRoundTrips = 80
        });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.True(client.Inputs[^1].Count <= 120);
        Assert.All(client.Inputs, input => Assert.Single(NavigationGuidanceInputs(input)));
        Assert.Contains(NavigationGuidance.Read("maui"),
            Assert.Single(NavigationGuidanceInputs(client.Inputs[^1])), StringComparison.Ordinal);
    }

    private static ToolCallResult NavigationObservation(string framework)
        => new(false, new JsonObject
        {
            ["isError"] = false,
            ["result"] = new JsonObject
            {
                ["capability"] = "ui.observe",
                ["framework"] = framework,
                ["graphStructureControllers"] = new JsonArray(new JsonObject { ["framework"] = framework }),
                ["root"] = new JsonObject { ["id"] = "page" }
            }
        }.ToJsonString(), "Observed current UI.");

    private static IEnumerable<string> NavigationGuidanceInputs(JsonArray input)
        => input.OfType<JsonObject>()
            .Where(item => item["role"]?.GetValue<string>() == "developer" && item["content"] is JsonArray)
            .SelectMany(item => item["content"]!.AsArray().OfType<JsonObject>())
            .Select(content => content["text"]?.GetValue<string>() ?? string.Empty)
            .Where(text => text.StartsWith("Navigation guidance for ", StringComparison.Ordinal));
}
