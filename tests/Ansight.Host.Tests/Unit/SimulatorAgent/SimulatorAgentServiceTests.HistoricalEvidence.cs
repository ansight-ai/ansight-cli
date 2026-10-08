using System.Text.Json.Nodes;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentServiceTests
{
    [Fact]
    public async Task RunAsync_HttpKeepsDestinationEvidenceAfterTaskAndTaskChecksAfterNextCall()
    {
        var client = new FakeOpenAiClient([
            CreateFunctionTurn("open", "ansight_tap_ui", new JsonObject { ["text"] = "Eagle Rock" }),
            CreateFunctionTurn("copy", "ansight_run_task", new JsonObject { ["taskId"] = "copy-gps-location" }),
            CreateFunctionTurn("remaining", "ansight_assert_ui", new JsonObject { ["text"] = "Ready" }),
            CreateFunctionTurn("complete", "complete_instruction", new JsonObject { ["outcome"] = "succeeded", ["summary"] = "Verified." })
        ]);
        const string navigation = """
            {"isError":false,"result":{"capability":"ui.tap","performed":true,"message":"Tap delivered.",
              "afterObservation":{"capturedAtUtc":"2026-10-08T08:35:16Z","truncated":true,"root":{
                "type":"Application","children":[
                  {"nodeId":"stale-id","text":"Eagle Rock","role":"text","type":"StaticText","visible":true,"bounds":[0,0,1,1]},
                  {"automationId":"area-tabs-toolbar-detail-tab","text":"About, selected","role":"button","visible":true}
                ]}}}}
            """;
        const string task = """
            {"isError":false,"result":{"taskId":"copy-gps-location","status":"Passed",
              "output":{"summary":"Copied and verified GPS coordinates.","clipboardText":"34.1432,-118.1835"},
              "assertions":[{"assertionId":"clipboard-matches-displayed-gps","passed":true,"message":"Copied coordinates match the card."}]}}
            """;
        var gateway = new FakeToolGateway();
        gateway.Results.Enqueue(new ToolCallResult(false, navigation, "Tap delivered."));
        gateway.Results.Enqueue(new ToolCallResult(false, task, "Task passed."));
        gateway.Results.Enqueue(new ToolCallResult(false, "{\"passed\":true}", "Ready is visible."));
        using var service = new SimulatorAgentService(new InMemoryEncryptedStorage(), client, gateway);
        service.SetDefaultModelAccessTokenForTesting("sk-local-test");

        var result = await service.RunAsync(new SimulatorAgentRunRequest("session-123",
            ["Open Eagle Rock, verify the destination, copy its GPS, then verify Ready."]) { CaptureTrace = true });

        Assert.Equal(SimulatorAgentRunStatus.Succeeded, result.Status);
        var previousNavigation = ReadOutput(client.Inputs[2], "open");
        Assert.True(previousNavigation["superseded"]!.GetValue<bool>());
        var observed = previousNavigation["historicalEvidence"]!["observation"]!;
        Assert.Equal("2026-10-08T08:35:16Z", observed["capturedAtUtc"]!.GetValue<string>());
        Assert.True(observed["truncated"]!.GetValue<bool>());
        Assert.Contains(observed["nodes"]!.AsArray(), node => node?["text"]?.GetValue<string>() == "Eagle Rock"
            && node["role"]?.GetValue<string>() == "text" && node["visible"]?.GetValue<bool>() == true);
        Assert.DoesNotContain("stale-id", previousNavigation.ToJsonString());
        Assert.DoesNotContain("bounds", previousNavigation.ToJsonString());

        var previousTask = ReadOutput(client.Inputs[3], "copy")["historicalEvidence"]!;
        Assert.Equal("Passed", previousTask["status"]!.GetValue<string>());
        Assert.Equal("Copied and verified GPS coordinates.", previousTask["output"]!["summary"]!.GetValue<string>());
        Assert.True(previousTask["assertions"]![0]!["passed"]!.GetValue<bool>());
        Assert.Equal(navigation, Assert.Single(result.Audit.ToolCalls, call => call.CallId == "open").Result.Content);
        Assert.Equal(task, Assert.Single(result.Audit.ToolCalls, call => call.CallId == "copy").Result.Content);
    }

    [Fact]
    public void HistoricalEvidence_PreservesFailuresAndBoundsLargeTaskResults()
    {
        var result = JsonNode.Parse("""
            {"isError":true,"result":{"taskId":"verify","status":"Failed",
              "assertions":[{"assertionId":"destination-visible","passed":false,"message":"Destination did not appear."}],
              "output":{"summary":"Could not open the destination."}}}
            """)!.AsObject();
        result["result"]!["output"]!["largeArtifact"] = new string('x', 50_000);
        var original = result.ToJsonString();

        var summary = ToolEvidenceSummary.Create(result)!;

        Assert.Equal("Failed", summary["status"]!.GetValue<string>());
        Assert.False(summary["assertions"]![0]!["passed"]!.GetValue<bool>());
        Assert.True(summary["summaryTruncated"]!.GetValue<bool>());
        Assert.Equal("Could not open the destination.", summary["output"]!["summary"]!.GetValue<string>());
        Assert.True(summary.ToJsonString().Length <= 10_000);
        Assert.Equal(original, result.ToJsonString());
    }

    [Fact]
    public void HistoricalEvidence_BatchKeepsEachStepsOutcomeAndMarksShortenedObservations()
    {
        var nodes = new JsonArray(Enumerable.Range(0, 100).Select(index => (JsonNode?)new JsonObject
        {
            ["text"] = $"Node {index} " + new string('x', 500), ["role"] = "text", ["visible"] = true
        }).ToArray());
        var result = JsonNode.Parse("""
            {"isError":true,"steps":[
              {"step":1,"toolName":"ansight_tap_ui","isError":false,"result":{"result":{"capability":"ui.tap","performed":true}}},
              {"step":2,"toolName":"ansight_wait_for_ui","isError":true,"result":{"result":{"capability":"ui.wait_for","condition":"visible","satisfied":false,"selector":{"text":"Ready"}}}}
            ]}
            """)!.AsObject();
        result["steps"]![1]!["result"]!["result"]!["afterObservation"] = new JsonObject
        { ["root"] = new JsonObject { ["children"] = nodes } };

        var summary = ToolEvidenceSummary.Create(result)!;

        Assert.True(summary["isError"]!.GetValue<bool>());
        Assert.True(summary.ToJsonString().Length <= 10_000);
        // The first step's delivered action cannot become proof that the following wait passed.
        Assert.True(summary["steps"]![0]!["performed"]!.GetValue<bool>());
        Assert.False(summary["steps"]![1]!["satisfied"]!.GetValue<bool>());
        Assert.True(summary["steps"]![1]!["isError"]!.GetValue<bool>());
        Assert.Equal("Ready", summary["steps"]![1]!["selector"]!["text"]!.GetValue<string>());
        Assert.True(summary["summaryTruncated"]?.GetValue<bool>() == true
            || summary["steps"]![1]!["summaryTruncated"]?.GetValue<bool>() == true);
    }

    private static JsonObject ReadOutput(JsonArray input, string callId)
    {
        var item = Assert.Single(input.OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "function_call_output"
            && item["call_id"]?.GetValue<string>() == callId);
        return JsonNode.Parse(item["output"]!.GetValue<string>())!.AsObject();
    }
}
