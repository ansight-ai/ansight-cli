using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    private const string TaskBatchReason = "Map is open; the scenario supplies every input. Reset preserves the Map tab, focus verifies the requested selection, and validation resolves its own IDs without another decision.";

    [Fact]
    public async Task RunAsync_TaskBatchExecutesTheMapSequenceInOnePassWithReasonAndPerTaskEvidence()
    {
        var gateway = TaskBatchGateway();
        var taskIds = new[] { "reset-map-state", "focus-map-search-area", "validate-map-area-snapshot" };
        foreach (var taskId in taskIds)
        {
            var assertions = new[] { new RepositoryTaskAssertion(taskId, true, $"Verified {taskId}.", JsonValue.Create(true), JsonValue.Create(true)) };
            gateway.Results.Enqueue(new ToolCallResult(false,
                new JsonObject { ["result"] = new JsonObject { ["taskId"] = taskId, ["status"] = "Passed", ["assertions"] = new JsonArray(new JsonObject { ["assertionId"] = taskId, ["passed"] = true }), ["internalTrace"] = "retained-only" } }.ToJsonString(),
                $"Task {taskId} passed.")
            {
                ModelOutput = new JsonObject { ["result"] = new JsonObject { ["taskId"] = taskId, ["status"] = "Passed", ["assertions"] = new JsonArray(new JsonObject { ["assertionId"] = taskId, ["passed"] = true }) } }.ToJsonString(),
                TaskAssertions = assertions,
                TaskCalls = [new RepositoryTaskToolCall(1, "app.query", DateTimeOffset.UtcNow, 1, false, "Verified app state.")],
                TaskSource = new RepositoryTaskSourceTrace(taskId, [])
            });
        }
        var client = new FakeOpenAiClient([TaskBatchTurn(), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new List<SimulatorAgentProgress>();

        var result = await service.RunAsync(TaskBatchRequest(), new SynchronousProgress<SimulatorAgentProgress>(progress.Add));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(2, client.Inputs.Count);
        Assert.Equal(taskIds, gateway.Calls.Select(call => call.Arguments["taskId"]!.GetValue<string>()));
        Assert.All(gateway.Calls, call => { Assert.Equal("ansight_run_task", call.ToolName); Assert.Equal("session-123", call.SessionId); });
        Assert.Equal(37.016575, gateway.Calls[0].Arguments["input"]!["latitude"]!.GetValue<double>());
        Assert.Equal("Point Perpendicular", gateway.Calls[1].Arguments["input"]!["searchArea"]!.GetValue<string>());
        Assert.Equal("Seaside", gateway.Calls[2].Arguments["input"]!["targetArea"]!.GetValue<string>());
        var steps = result.Audit.ToolCalls.Where(call => call.BatchCallId == "task-batch").ToArray();
        Assert.Equal(new int?[] { 1, 2, 3 }, steps.Select(call => call.BatchStepIndex));
        Assert.All(steps, call =>
        {
            Assert.Equal(1, call.InstructionTurn);
            Assert.Equal(3, call.BatchStepCount);
            Assert.True(Assert.Single(call.TaskAssertions!).Passed);
            Assert.Single(call.TaskCalls!);
            Assert.NotNull(call.TaskSource);
            Assert.Contains("retained-only", call.Result.Content);
        });
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        Assert.Equal(TaskBatchReason, JsonNode.Parse(batch.Result.Content)!["reason"]!.GetValue<string>());
        Assert.Contains(progress, item => item.ToolName == AgentTaskBatch.ToolName && item.Message.Contains(TaskBatchReason));
        var output = Assert.Single(client.Inputs[1].OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal("task-batch", output["call_id"]!.GetValue<string>());
        Assert.Contains(TaskBatchReason, output["output"]!.GetValue<string>());
        Assert.DoesNotContain("retained-only", output["output"]!.GetValue<string>());
        Assert.Equal(3, JsonNode.Parse(output["output"]!.GetValue<string>())!["steps"]!.AsArray().Count);
    }

    [Fact]
    public async Task RunAsync_TaskBatchIsAvailableWithoutLoadingToolsAndReturnsOneWebSocketOutput()
    {
        var gateway = TaskBatchGateway();
        var session = new FakeOpenAiSession([TaskBatchTurn(), CompleteWebSocketTurn()]);
        using var service = CreateToolLoadingService(session, gateway);

        var result = await service.RunAsync(ToolLoadingRequest("Run reset-map-state, focus-map-search-area, then validate-map-area-snapshot.") with { CaptureTrace = true });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Contains(AgentTaskBatch.ToolName, ToolNames(session.Requests[0].Tools));
        Assert.Equal(3, gateway.Calls.Count);
        var output = Assert.Single(session.Requests[1].IncrementalInput!.OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "function_call_output");
        Assert.Equal("task-batch", output["call_id"]!.GetValue<string>());
        Assert.Contains("validate-map-area-snapshot", output["output"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Rejected")]
    [InlineData("TimedOut")]
    [InlineData("Inconclusive")]
    public async Task RunAsync_TaskBatchStopsAtTheFirstUnsuccessfulTask(string status)
    {
        var gateway = TaskBatchGateway();
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"status\":\"Passed\"}", "Reset passed."));
        gateway.Results.Enqueue(new ToolCallResult(true, new JsonObject { ["status"] = status }.ToJsonString(), $"Focus {status}."));
        var client = new FakeOpenAiClient([TaskBatchTurn(), CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(TaskBatchRequest());

        Assert.Equal(2, gateway.Calls.Count);
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        Assert.True(batch.IsError);
        var output = JsonNode.Parse(batch.Result.Content)!;
        Assert.False(output["completed"]!.GetValue<bool>());
        Assert.Equal(1, output["skippedStepCount"]!.GetValue<int>());
        Assert.Equal("validate-map-area-snapshot", output["skippedSteps"]![0]!["taskId"]!.GetValue<string>());
        Assert.Contains("Reset passed", batch.Result.Content);
        Assert.Contains($"Focus {status}", batch.Result.Content);
    }

    [Theory]
    [InlineData("missing-reason")]
    [InlineData("blank-reason")]
    [InlineData("long-reason")]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("missing-id")]
    [InlineData("bad-input")]
    [InlineData("ui-step")]
    [InlineData("nested-batch")]
    [InlineData("completion")]
    [InlineData("target-override")]
    [InlineData("negative-cooldown")]
    [InlineData("long-cooldown")]
    [InlineData("fractional-cooldown")]
    [InlineData("string-cooldown")]
    public async Task RunAsync_TaskBatchRejectsMalformedPlansBeforeAnyTask(string error)
    {
        var turn = TaskBatchTurn();
        var arguments = turn.FunctionCalls[0].Arguments;
        var steps = arguments["steps"]!.AsArray();
        var last = steps[2]!.AsObject();
        switch (error)
        {
            case "missing-reason": arguments.Remove("reason"); break;
            case "blank-reason": arguments["reason"] = "  "; break;
            case "long-reason": arguments["reason"] = new string('x', 1001); break;
            case "empty": steps.Clear(); break;
            case "too-many": while (steps.Count < 9) steps.Add(last.DeepClone()); break;
            case "missing-id": last.Remove("taskId"); break;
            case "bad-input": last["input"] = "invalid"; break;
            case "ui-step": last["toolName"] = "ansight_tap_ui"; break;
            case "nested-batch": last["toolName"] = AgentTaskBatch.ToolName; break;
            case "completion": last["toolName"] = "complete_instruction"; break;
            case "target-override": last["sessionId"] = "another-session"; break;
            case "negative-cooldown": last["cooldownMs"] = -1; break;
            case "long-cooldown": last["cooldownMs"] = 10001; break;
            case "fractional-cooldown": last["cooldownMs"] = 1.5; break;
            case "string-cooldown": last["cooldownMs"] = "100"; break;
        }
        var gateway = TaskBatchGateway();
        var client = new FakeOpenAiClient([turn, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(TaskBatchRequest());

        Assert.Empty(gateway.Calls);
        Assert.Contains(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName && call.IsError && call.Result.Content.Contains("batchGuard"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_TaskBatchRetainsPartialResultsWhenBudgetOrCancellationStopsIt(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var gateway = TaskBatchGateway();
        var client = new FakeOpenAiClient([TaskBatchTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new SynchronousProgress<SimulatorAgentProgress>(item =>
        {
            if (cancel && item.Stage == SimulatorAgentProgressStage.ToolCompleted && item.ToolName == "ansight_run_task") cancellation.Cancel();
        });

        var result = await service.RunAsync(TaskBatchRequest() with { MaximumToolCalls = cancel ? 10 : 1 }, progress, cancellation.Token);

        Assert.Equal(cancel ? SimulatorAgentRunStatus.Cancelled : SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Single(gateway.Calls);
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        Assert.True(batch.IsError);
        var output = JsonNode.Parse(batch.Result.Content)!;
        Assert.Equal(1, output["executedStepCount"]!.GetValue<int>());
        Assert.Equal(2, output["skippedStepCount"]!.GetValue<int>());
        Assert.Contains(TaskBatchReason, batch.Message);
    }

    [Fact]
    public async Task RunAsync_TaskBatchCannotBypassTheRepeatedFailureGuard()
    {
        var gateway = TaskBatchGateway();
        gateway.Results.Enqueue(new ToolCallResult(true, "{}", "Reset failed."));
        gateway.Results.Enqueue(new ToolCallResult(true, "{}", "Reset failed."));
        var batch = TaskBatchTurn();
        var arguments = batch.FunctionCalls[0].Arguments["steps"]![0]!.DeepClone().AsObject();
        var client = new FakeOpenAiClient([
            CreateFunctionTurn("first", "ansight_run_task", arguments.DeepClone().AsObject()),
            CreateFunctionTurn("second", "ansight_run_task", arguments.DeepClone().AsObject()),
            batch, CompleteWebSocketTurn()
        ]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(TaskBatchRequest());

        Assert.Equal(2, gateway.Calls.Count);
        var blocked = Assert.Single(result.Audit.ToolCalls, call => call.BatchStepIndex == 1);
        Assert.True(blocked.IsError);
        Assert.Contains("circuitBreaker", blocked.Result.Content);
        Assert.DoesNotContain(result.Audit.ToolCalls, call => call.BatchStepIndex == 2);
    }

    [Fact]
    public void HistoricalEvidence_TaskBatchKeepsTaskIdentityAssertionsAndSkippedCount()
    {
        var output = JsonNode.Parse("""
            {"isError":true,"completed":false,"reason":"Known inputs and verified prerequisites.","requestedStepCount":3,"executedStepCount":2,"skippedStepCount":1,"steps":[
              {"step":1,"toolName":"ansight_run_task","taskId":"reset","isError":false,"result":{"result":{"status":"Passed","assertions":[{"assertionId":"reset-verified","passed":true}]}}},
              {"step":2,"toolName":"ansight_run_task","taskId":"focus","isError":true,"result":{"batchGuard":true,"isError":true}}
            ]}
            """)!.AsObject();

        var evidence = ToolEvidenceSummary.Create(output)!;

        Assert.Equal(1, evidence["skippedStepCount"]!.GetValue<int>());
        Assert.False(evidence["completed"]!.GetValue<bool>());
        Assert.Equal("Known inputs and verified prerequisites.", evidence["reason"]!.GetValue<string>());
        Assert.Equal("reset", evidence["steps"]![0]!["taskId"]!.GetValue<string>());
        Assert.True(evidence["steps"]![0]!["assertions"]![0]!["passed"]!.GetValue<bool>());
        Assert.Equal("focus", evidence["steps"]![1]!["taskId"]!.GetValue<string>());
        Assert.True(evidence["steps"]![1]!["isError"]!.GetValue<bool>());
    }

    [Fact(Timeout = 5000)]
    public async Task RunAsync_TaskBatchWaitsOnlyBetweenSuccessfulTasksAndRecordsCooldown()
    {
        var turn = TaskBatchTurn();
        turn.FunctionCalls[0].Arguments["steps"]![0]!["cooldownMs"] = 50;
        // A final task's requested cooldown must not delay completion.
        turn.FunctionCalls[0].Arguments["steps"]![2]!["cooldownMs"] = 10000;
        var gateway = TaskBatchGateway();
        var client = new FakeOpenAiClient([turn, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new List<SimulatorAgentProgress>();

        var result = await service.RunAsync(TaskBatchRequest(), new SynchronousProgress<SimulatorAgentProgress>(progress.Add));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.Equal(3, gateway.Calls.Count);
        Assert.All(gateway.Calls, call => Assert.False(call.Arguments.ContainsKey("cooldownMs")));
        var waiting = Assert.Single(progress, item => item.Message.StartsWith("Task batch cooldown:"));
        Assert.Equal(AgentTaskBatch.ToolName, waiting.ToolName);
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        var output = JsonNode.Parse(batch.Result.Content)!;
        var cooldown = output["steps"]![0]!["cooldown"]!;
        Assert.Equal(50, cooldown["requestedMilliseconds"]!.GetValue<int>());
        Assert.True(cooldown["elapsedMilliseconds"]!.GetValue<long>() >= 40);
        Assert.Equal("completed", cooldown["status"]!.GetValue<string>());
        Assert.Null(output["steps"]![1]!["cooldown"]);
        Assert.Null(output["steps"]![2]!["cooldown"]);
        Assert.Equal(2, client.Inputs.Count);
    }

    [Fact(Timeout = 5000)]
    public async Task RunAsync_TaskBatchCancellationDuringCooldownSkipsTheNextTask()
    {
        using var cancellation = new CancellationTokenSource();
        var turn = TaskBatchTurn();
        turn.FunctionCalls[0].Arguments["steps"]![0]!["cooldownMs"] = 10000;
        var gateway = TaskBatchGateway();
        var client = new FakeOpenAiClient([turn]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new SynchronousProgress<SimulatorAgentProgress>(item =>
        {
            if (item.Message.StartsWith("Task batch cooldown:")) cancellation.CancelAfter(20);
        });

        var result = await service.RunAsync(TaskBatchRequest(), progress, cancellation.Token);

        Assert.Equal(SimulatorAgentRunStatus.Cancelled, result.Status);
        Assert.Single(gateway.Calls);
        Assert.Single(result.Audit.ToolCalls, call => call.BatchCallId == "task-batch");
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        var output = JsonNode.Parse(batch.Result.Content)!;
        Assert.Equal(2, output["skippedStepCount"]!.GetValue<int>());
        Assert.Equal("cancelled", output["steps"]![0]!["cooldown"]!["status"]!.GetValue<string>());
        Assert.True(output["steps"]![0]!["cooldown"]!["elapsedMilliseconds"]!.GetValue<long>() < 10000);
    }

    [Theory(Timeout = 5000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_TaskBatchDoesNotWaitWhenFailureOrBudgetPreventsTheNextTask(bool failed)
    {
        var turn = TaskBatchTurn();
        turn.FunctionCalls[0].Arguments["steps"]![0]!["cooldownMs"] = 10000;
        var gateway = TaskBatchGateway();
        if (failed) gateway.Results.Enqueue(new ToolCallResult(true, "{}", "Reset failed."));
        var client = new FakeOpenAiClient([turn, CompleteWebSocketTurn()]);
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");
        var progress = new List<SimulatorAgentProgress>();

        var result = await service.RunAsync(TaskBatchRequest() with { MaximumToolCalls = failed ? 10 : 1 },
            new SynchronousProgress<SimulatorAgentProgress>(progress.Add));

        Assert.Single(gateway.Calls);
        Assert.DoesNotContain(progress, item => item.Message.StartsWith("Task batch cooldown:"));
        var batch = Assert.Single(result.Audit.ToolCalls, call => call.ToolName == AgentTaskBatch.ToolName);
        var output = JsonNode.Parse(batch.Result.Content)!;
        Assert.Equal(2, output["skippedStepCount"]!.GetValue<int>());
        Assert.Null(output["steps"]![0]!["cooldown"]);
    }

    private static FakeToolGateway TaskBatchGateway()
    {
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [
                CreateRepositoryTaskShortcut("ansight_task_reset", "reset-map-state", "Reset map"),
                CreateRepositoryTaskShortcut("ansight_task_focus", "focus-map-search-area", "Focus area"),
                CreateRepositoryTaskShortcut("ansight_task_validate", "validate-map-area-snapshot", "Validate areas")]
        };
        AddToolLoadingDefinitions(gateway);
        return gateway;
    }

    private static SimulatorAgentRunRequest TaskBatchRequest()
        => new("session-123", ["Run reset-map-state, focus-map-search-area, then validate-map-area-snapshot."]) { CaptureTrace = true };

    private static OpenAiTurn TaskBatchTurn()
        => CreateFunctionTurn("task-batch", AgentTaskBatch.ToolName, new JsonObject
        {
            ["reason"] = TaskBatchReason,
            ["steps"] = new JsonArray(
                new JsonObject { ["taskId"] = "reset-map-state", ["input"] = new JsonObject { ["latitude"] = 37.016575, ["longitude"] = 26.97943, ["radiusMeters"] = 7119, ["bearing"] = 0, ["pitch"] = 0, ["animate"] = false, ["durationMs"] = 0 } },
                new JsonObject { ["taskId"] = "focus-map-search-area", ["input"] = new JsonObject { ["searchArea"] = "Point Perpendicular" } },
                new JsonObject { ["taskId"] = "validate-map-area-snapshot", ["input"] = new JsonObject { ["searchArea"] = "Point Perpendicular", ["targetArea"] = "Seaside" } })
        });
}
