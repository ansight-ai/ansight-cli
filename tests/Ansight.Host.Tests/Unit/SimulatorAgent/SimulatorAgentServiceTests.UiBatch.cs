using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Theory]
    [InlineData("ansight_tap_ui")]
    [InlineData("ansight_type_text")]
    public async Task RunAsync_BatchFindActsAndWaitsInOneModelPassWithSeparateAudit(string action)
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(1));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Input delivered."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"satisfied\":true}}", "Results ready."));
        var client = new FakeOpenAiClient([BatchTurn(action), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, client.Inputs.Count);
        Assert.Equal(new[] { "ansight_find_ui", action, "ansight_wait_for_ui" }, gateway.Calls.Select(call => call.ToolName));
        Assert.Equal("search-input", gateway.Calls[1].Arguments["automationId"]!.GetValue<string>());
        Assert.Equal("fresh-target", gateway.Calls[1].Arguments["targetFingerprint"]!.GetValue<string>());
        Assert.Equal(0, gateway.Calls[1].Arguments["index"]!.GetValue<int>());
        if (action == "ansight_type_text") Assert.Equal("Eagle Rock", gateway.Calls[1].Arguments["value"]!.GetValue<string>());
        var steps = result.Audit.ToolCalls.Where(call => call.BatchCallId == "batch").ToArray();
        Assert.Equal(3, steps.Length);
        Assert.Equal(new int?[] { 1, 2, 3 }, steps.Select(call => call.BatchStepIndex));
        Assert.All(steps, call => { Assert.Equal(1, call.InstructionTurn); Assert.Equal(3, call.BatchStepCount); });
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentUiBatch.ToolName);
        Assert.Contains("Completed all 3 batch steps", batch.Message);
        if (action == "ansight_type_text") Assert.DoesNotContain("Eagle Rock", batch.Arguments.Content);
        var outputs = client.Inputs[1].OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "function_call_output").ToArray();
        Assert.Equal("batch", Assert.Single(outputs)["call_id"]!.GetValue<string>());
        Assert.Contains("Results ready", outputs[0]["output"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public async Task RunAsync_BatchStopsForMissingDuplicateOrTruncatedMatches(int total, bool truncated)
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(total, truncated));
        var client = new FakeOpenAiClient([BatchTurn("ansight_tap_ui"), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Equal("ansight_find_ui", Assert.Single(gateway.Calls).ToolName);
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentUiBatch.ToolName);
        Assert.Contains("ambiguous", batch.Message);
        var output = JsonNode.Parse(batch.Result.Content)!;
        Assert.False(output["completed"]!.GetValue<bool>());
        Assert.Equal(2, output["skippedStepCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_BatchUsesLoadedToolsAndReturnsOneMatchingWebSocketOutput()
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(1));
        var session = new FakeOpenAiSession([
            BatchTurn("ansight_type_text", "premature"), LoadManualToolsTurn("load"),
            BatchTurn("ansight_type_text"), CompleteWebSocketTurn()
        ]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Search for Eagle Rock.") with { CaptureTrace = true });

        Assert.Equal(3, gateway.Calls.Count);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "premature").IsError);
        var declaration = Assert.Single(AdditionalToolItems(session.Requests[2].IncrementalInput!));
        Assert.Contains(AgentUiBatch.ToolName, ToolNames(declaration["tools"]!.AsArray()));
        var outputs = session.Requests[3].IncrementalInput!.OfType<JsonObject>()
            .Where(item => item["type"]?.GetValue<string>() == "function_call_output").ToArray();
        Assert.Equal("batch", Assert.Single(outputs)["call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_BatchTypesIntoKnownFieldWithoutRedundantFindOrTap()
    {
        var gateway = BatchGateway();
        var batch = CreateFunctionTurn("batch", AgentUiBatch.ToolName, new JsonObject
        {
            ["steps"] = new JsonArray(
                new JsonObject { ["toolName"] = "ansight_type_text", ["arguments"] = new JsonObject { ["automationId"] = "search-input", ["value"] = "Eagle Rock" } },
                new JsonObject { ["toolName"] = "ansight_wait_for_ui", ["arguments"] = new JsonObject { ["automationId"] = "search-loading", ["condition"] = "hidden" } })
        });
        var client = new FakeOpenAiClient([batch, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(new[] { "ansight_type_text", "ansight_wait_for_ui" }, gateway.Calls.Select(call => call.ToolName));
    }

    [Theory]
    [InlineData("automationId")]
    [InlineData("index")]
    [InlineData("normalizedX")]
    [InlineData("targetFingerprint")]
    public async Task RunAsync_BatchRejectsDiscoveredTargetOverridesBeforeAnyAction(string property)
    {
        var gateway = BatchGateway();
        var batch = BatchTurn("ansight_type_text");
        batch.FunctionCalls[0].Arguments["steps"]![1]!["arguments"]![property] = "override";
        var client = new FakeOpenAiClient([batch, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Empty(gateway.Calls);
        Assert.Contains(result.Audit.ToolCalls, call => call.IsError && call.Message.Contains("selector and coordinate overrides"));
    }

    [Fact]
    public async Task RunAsync_BatchDoesNotInventTargetWhenFindHasNoHint()
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(new ToolCallResult(false,
            "{\"result\":{\"totalMatches\":1,\"matches\":[{\"text\":\"Search\"}]}}", "Found label without an actionable hint."));
        var client = new FakeOpenAiClient([BatchTurn("ansight_type_text"), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Single(gateway.Calls);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.BatchStepIndex == 2).IsError);
    }

    [Fact]
    public async Task RunAsync_BatchFailureDoesNotExecuteRemainingSteps()
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(1));
        gateway.Results.Enqueue(new ToolCallResult(true, "{\"isError\":true}", "Target changed."));
        var client = new FakeOpenAiClient([BatchTurn("ansight_type_text"), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Equal(2, gateway.Calls.Count);
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentUiBatch.ToolName);
        Assert.True(batch.IsError);
        Assert.Contains("Target changed", batch.Message);
        Assert.Equal(1, JsonNode.Parse(batch.Result.Content)!["skippedStepCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_BatchCannotBypassTaskCoverageGuard()
    {
        var gateway = BatchGateway([CreateRepositoryTaskShortcut("ansight_task_search", "search", "Search")]);
        gateway.Results.Enqueue(BatchFindResult(1));
        var client = new FakeOpenAiClient([BatchTurn("ansight_type_text"), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Single(gateway.Calls);
        var blocked = Assert.Single(result.Audit.ToolCalls, call => call.BatchStepIndex == 2);
        Assert.True(blocked.IsError);
        Assert.Contains("taskReassessmentGuard", blocked.Result.Content);
    }

    [Fact]
    public async Task RunAsync_BatchChargesEachStepAgainstToolBudget()
    {
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(1));
        var client = new FakeOpenAiClient([BatchTurn("ansight_type_text")]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest() with { MaximumToolCalls = 1 });

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Single(gateway.Calls);
        Assert.Contains("1-tool-call run limit", result.Message);
    }

    [Fact]
    public async Task RunAsync_BatchCancellationStopsBeforeNextAction()
    {
        using var cancellation = new CancellationTokenSource();
        var gateway = BatchGateway();
        gateway.Results.Enqueue(BatchFindResult(1));
        var client = new FakeOpenAiClient([BatchTurn("ansight_type_text")]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new SynchronousProgress<SimulatorAgentProgress>(step =>
        {
            if (step.Stage == SimulatorAgentProgressStage.ToolCompleted && step.ToolName == "ansight_find_ui") cancellation.Cancel();
        });

        var result = await service.RunAsync(BatchRequest(), progress, cancellation.Token);

        Assert.Equal(SimulatorAgentRunStatus.Cancelled, result.Status);
        Assert.Single(gateway.Calls);
    }

    [Theory]
    [InlineData("complete_instruction")]
    [InlineData("ansight_run_task")]
    [InlineData("ansight_run_ui_batch")]
    [InlineData("ansight_launch_app")]
    public async Task RunAsync_BatchRejectsUnsupportedStepsBeforeAnyAction(string toolName)
    {
        var gateway = BatchGateway();
        var turn = BatchTurn("ansight_tap_ui");
        turn.FunctionCalls[0].Arguments["steps"]![2]!["toolName"] = toolName;
        var client = new FakeOpenAiClient([turn, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(BatchRequest());

        Assert.Empty(gateway.Calls);
        Assert.Contains(result.Audit.ToolCalls, call => call.ToolName == AgentUiBatch.ToolName && call.IsError);
    }

    private static SimulatorAgentRunRequest BatchRequest()
        => new("session-123", ["Search for Eagle Rock and wait for results."]) { CaptureTrace = true };

    private static FakeToolGateway BatchGateway(IReadOnlyList<RepositoryTaskShortcut>? tasks = null)
    {
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = tasks ?? [] };
        AddToolLoadingDefinitions(gateway, "ansight_find_ui", "ansight_tap_ui", "ansight_type_text", "ansight_wait_for_ui");
        return gateway;
    }

    private static OpenAiTurn BatchTurn(string action, string callId = "batch")
        => CreateFunctionTurn(callId, AgentUiBatch.ToolName, new JsonObject
        {
            ["steps"] = new JsonArray(
                new JsonObject { ["toolName"] = "ansight_find_ui", ["arguments"] = new JsonObject { ["automationId"] = "search-input" } },
                new JsonObject { ["toolName"] = action, ["usePreviousTarget"] = true,
                    ["arguments"] = action == "ansight_type_text" ? new JsonObject { ["value"] = "Eagle Rock" } : new JsonObject() },
                new JsonObject { ["toolName"] = "ansight_wait_for_ui", ["arguments"] = new JsonObject { ["automationId"] = "search-loading", ["condition"] = "hidden" } })
        });

    private static ToolCallResult BatchFindResult(int total, bool truncated = false)
        => new(false, new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["totalMatches"] = total, ["truncated"] = truncated,
                ["matches"] = total == 0 ? new JsonArray() : new JsonArray(new JsonObject
                {
                    ["tapHint"] = new JsonObject { ["selector"] = new JsonObject
                    { ["automationId"] = "search-input", ["targetFingerprint"] = "fresh-target", ["index"] = 0 } }
                })
            }
        }.ToJsonString(), "Found matches.");
}
