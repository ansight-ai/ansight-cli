using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsync_AllowsTemporaryStartingStateWithOtherLexicallyMatchingTasks()
    {
        const string focusTaskId = "focus-map-search-area";
        const string detailsTaskId = "open-selected-map-area-details";
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", new JsonObject
            {
                ["uncoveredStep"] = "Select the Map tab before searching for an area and opening Details.",
                ["reason"] = "starting-state-not-satisfied",
                ["relatedTaskId"] = focusTaskId,
                ["evidence"] = "For you is selected; both tasks require the Map tab or a selected map card.",
                ["consideredTaskIds"] = new JsonArray(focusTaskId, detailsTaskId)
            }),
            CreateFunctionTurn("select-map", "ansight_tap_ui", new JsonObject { ["automationId"] = "home-tab-explore" }),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut("ansight_task_focus", focusTaskId, "Focus map search area"),
                CreateRepositoryTaskShortcut("ansight_task_details", detailsTaskId, "Open selected map area details")
            ]
        };
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Map tab selected."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Select Map, search for an area, and open Details."]));

        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "declare").IsError);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "select-map").IsError);
        Assert.Equal("ansight_tap_ui", Assert.Single(gateway.Calls).ToolName);
    }

    [Fact]
    public async Task RunAsync_DoesNotForceWeatherTaskForLocationSearch()
    {
        const string weatherTaskId = "load-area-weather";
        var weatherTask = CreateRepositoryTaskShortcut("ansight_task_weather", weatherTaskId,
            "Load area weather") with
        {
            Description = "From the current area's open AreaPage, select About, scroll its details until the weather card is visible. Does not search for another area."
        };
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", new JsonObject
            {
                ["uncoveredStep"] = "Search Approach in the area details page and copy the Location card contents.",
                ["reason"] = "no-matching-task",
                ["relatedTaskId"] = null,
                ["evidence"] = "Weather is unrelated to Location sharing and clipboard copying.",
                ["consideredTaskIds"] = new JsonArray(weatherTaskId)
            }),
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject { ["value"] = "Approach" }),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = [weatherTask] };
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Typed."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search Approach and copy the Location card contents."]));

        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "declare").IsError);
        var typed = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "type");
        Assert.False(typed.IsError, typed.Message);
        Assert.DoesNotContain(gateway.Calls, call => call.ToolName == "ansight_run_task");
    }

    [Fact]
    public async Task RunAsync_TappingTextFieldDoesNotRequireAnotherTaskDeclaration()
    {
        const string taskId = "export-account-data";
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", UnrelatedTaskDeclaration(taskId)),
            CreateFunctionTurn("focus-search", "ansight_tap_ui", new JsonObject
            {
                ["text"] = "Search",
                ["role"] = "textbox"
            }),
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject { ["value"] = "Kalymnos" }),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_export", taskId, "Export account data")]
        };
        gateway.Results.Enqueue(new ToolCallResult(false,
            "{\"result\":{\"performed\":true,\"target\":{\"role\":\"textbox\"}}", "Search focused."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Typed."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search for Kalymnos."]) { CaptureTrace = true });

        var declared = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "declare");
        Assert.Contains("\"manualUiAllowed\":true", declared.Result.Content, StringComparison.Ordinal);
        var focused = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "focus-search");
        Assert.False(focused.IsError);
        Assert.Equal("{\"result\":{\"performed\":true,\"target\":{\"role\":\"textbox\"}}", focused.Result.Content);
        var typed = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "type");
        Assert.False(typed.IsError, typed.Message);
        Assert.Equal(2, gateway.Calls.Count);
    }

    [Theory]
    [InlineData("scope-mismatch")]
    [InlineData("missing-input")]
    public async Task RunAsync_ExcludesIncompatibleTaskForWholeInstruction(string reason)
    {
        const string taskId = "open-selected-map-area-details";
        var task = CreateRepositoryTaskShortcut("ansight_task_1_details", taskId,
            "Search an area, open its details, search for a child area and open that child") with
        {
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["targetArea"] = new JsonObject { ["type"] = "string" },
                    ["childArea"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("targetArea", "childArea")
            }
        };
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("exclude", "ansight_declare_uncovered_step", new JsonObject
            {
                ["uncoveredStep"] = "Search for Kalymnos, open a result, then return.",
                ["reason"] = reason,
                ["relatedTaskId"] = taskId,
                ["evidence"] = "The task additionally opens a child area. Required childArea is not supplied by the instruction or observations.",
                ["consideredTaskIds"] = new JsonArray(taskId)
            }),
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject { ["value"] = "Kalymnos" }),
            CreateFunctionTurn("open", "ansight_tap_ui", new JsonObject { ["automationId"] = "result-kalymnos" }),
            CreateFunctionTurn("back", "ansight_back_ui", new JsonObject()),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway { RepositoryTaskShortcuts = [task] };
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search for Kalymnos, open a result, then return."]));

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        Assert.All(result.Audit.ToolCalls, call => Assert.False(call.IsError, call.Message));
        Assert.Equal(3, gateway.Calls.Count);
        Assert.DoesNotContain(gateway.Calls, call => call.ToolName == "ansight_run_task");
    }

    [Fact]
    public async Task RunAsync_PreservesDeclarationThroughTypingAndRejectedSelectorButReassessesAfterNavigation()
    {
        const string taskId = "export-account-data";
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("declare", "ansight_declare_uncovered_step", UnrelatedTaskDeclaration(taskId)),
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject { ["value"] = "Kalymnos" }),
            CreateFunctionTurn("bad-selector", "ansight_tap_ui", new JsonObject { ["text"] = "Kalymnos", ["index"] = 1 }),
            CreateFunctionTurn("corrected-selector", "ansight_tap_ui", new JsonObject { ["automationId"] = "result-kalymnos" }),
            CreateFunctionTurn("blocked-after-navigation", "ansight_back_ui", new JsonObject()),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_1_export", taskId, "Export account data")]
        };
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Typed."));
        gateway.Results.Enqueue(new ToolCallResult(true,
            "{\"isError\":true,\"result\":{\"performed\":false,\"message\":\"No matching selector.\"}}", "No matching selector."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Opened."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search for Kalymnos, open a result, then return."]));

        Assert.Equal(3, gateway.Calls.Count);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "corrected-selector").IsError);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "blocked-after-navigation").IsError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ObservationsCannotResetStagnationThroughDeclarationsOrScreenshots(bool failedObservations)
    {
        const string taskId = "export-account-data";
        var turns = Enumerable.Range(0, 9).SelectMany(index => new[]
        {
            CreateFunctionTurn($"declare-{index}", "ansight_declare_uncovered_step", UnrelatedTaskDeclaration(taskId)),
            CreateFunctionTurn($"observe-{index}", index % 2 == 0 ? "ansight_find_ui" : "ansight_take_screenshot",
                new JsonObject { ["text"] = $"Result {index}" })
        }).ToArray();
        var client = new FakeOpenAiClient(turns);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts = [CreateRepositoryTaskShortcut("ansight_task_1_export", taskId, "Export account data")],
            Result = new ToolCallResult(failedObservations,
                failedObservations ? "{\"isError\":true}" : "{\"isError\":false}", "Unchanged observation.")
        };
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Open the requested result."])
        {
            CaptureTrace = true
        });

        Assert.Equal(SimulatorAgentRunStatus.Failed, result.Status);
        Assert.Equal(8, gateway.Calls.Count);
        Assert.Contains("stagnationGuard", result.Audit.ToolCalls.Last().Result.Content);
    }

    [Theory]
    [InlineData("textbox", false, true)]
    [InlineData("textbox", true, false)]
    [InlineData("text", false, false)]
    public async Task RunAsync_SearchWaitCannotVerifyOnlyTheInputEcho(string role, bool explicitInputSelector, bool rejected)
    {
        var waitArguments = new JsonObject { ["text"] = "Kalymnos", ["condition"] = "visible" };
        if (explicitInputSelector)
        {
            waitArguments["role"] = "textbox";
        }
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("type", "ansight_type_text", new JsonObject { ["value"] = "Kalymnos" }),
            CreateFunctionTurn("wait", "ansight_wait_for_ui", waitArguments),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"result\":{\"performed\":true}}", "Typed."));
        gateway.Results.Enqueue(new ToolCallResult(false, new JsonObject
        {
            ["isError"] = false,
            ["result"] = new JsonObject
            {
                ["satisfied"] = true,
                ["matches"] = new JsonArray(new JsonObject { ["text"] = "Kalymnos", ["role"] = role })
            }
        }.ToJsonString(), "Wait satisfied."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Search for Kalymnos, open a result, then return."]) { CaptureTrace = true });

        var wait = Assert.Single(result.Audit.ToolCalls, call => call.CallId == "wait");
        Assert.Equal(rejected, wait.IsError);
        if (rejected)
        {
            var content = JsonNode.Parse(wait.Result.Content)!["result"]!;
            Assert.False(content["satisfied"]!.GetValue<bool>());
            Assert.True(content["inputEchoOnly"]!.GetValue<bool>());
        }
    }

    private static OpenAiTurn CompleteNavigationTurn()
        => CreateFunctionTurn("complete", "complete_instruction", new JsonObject
        {
            ["outcome"] = "succeeded", ["summary"] = "Navigation fixture completed."
        });

    [Fact]
    public async Task RunAsync_CanExcludeMultipleOverlappingTasksWithoutUnlockingOtherUnassessedTasks()
    {
        var client = new FakeOpenAiClient(
        [
            CreateFunctionTurn("exclude-first", "ansight_declare_uncovered_step", Exclude("first", ["first", "second"])),
            CreateFunctionTurn("blocked", "ansight_tap_ui", new JsonObject { ["text"] = "Kalymnos" }),
            CreateFunctionTurn("exclude-second", "ansight_declare_uncovered_step", Exclude("second", ["second"])),
            CreateFunctionTurn("allowed", "ansight_tap_ui", new JsonObject { ["text"] = "Kalymnos" }),
            CompleteNavigationTurn()
        ]);
        var gateway = new FakeToolGateway
        {
            RepositoryTaskShortcuts =
            [
                CreateRepositoryTaskShortcut("ansight_task_1_first", "first", "Search Kalymnos open result and open a child area"),
                CreateRepositoryTaskShortcut("ansight_task_2_second", "second", "Search Kalymnos open result and save it")
            ]
        };
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123", ["Search Kalymnos open result."]));

        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "exclude-first").IsError);
        Assert.True(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "blocked").IsError);
        Assert.False(Assert.Single(result.Audit.ToolCalls, call => call.CallId == "exclude-second").IsError);
        Assert.Equal("ansight_tap_ui", Assert.Single(gateway.Calls).ToolName);

        static JsonObject Exclude(string taskId, string[] remaining)
            => new()
            {
                ["uncoveredStep"] = "Search Kalymnos open result.",
                ["reason"] = "scope-mismatch",
                ["relatedTaskId"] = taskId,
                ["evidence"] = "The task performs extra operations beyond opening the result.",
                ["consideredTaskIds"] = new JsonArray(remaining.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray())
            };
    }

    private static JsonObject UnrelatedTaskDeclaration(string taskId)
        => new()
        {
            ["uncoveredStep"] = "Search for Kalymnos and open the result.",
            ["reason"] = "no-matching-task",
            ["relatedTaskId"] = null,
            ["evidence"] = "Exporting account data does not cover this navigation.",
            ["consideredTaskIds"] = new JsonArray(taskId)
        };
}
