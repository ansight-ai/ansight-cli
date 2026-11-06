using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentToolGatewayTests
{
    [Fact]
    public async Task LargeOcrResponseKeepsCompleteTargetsWithinModelBudgetAndFullAudit()
    {
        var content = new JsonArray();

        for (var index = 0; index < 100; index++)
        {
            content.Add(new JsonObject
            {
                ["text"] = $"Route {index}: {new string('x', 180)}",
                ["confidence"] = 0.987654321,
                ["bounds"] = new JsonObject { ["x"] = 0.1234567, ["y"] = 0.2, ["width"] = 0.3, ["height"] = 0.02 },
                ["tapHint"] = new JsonObject
                {
                    ["tool"] = "ansight_tap_ui",
                    ["selector"] = new JsonObject { ["normalizedX"] = 0.123456789, ["normalizedY"] = 0.234567891 }
                }
            });
        }

        // Oversized individual strings must not become misleading partial selectors.
        content.Insert(0, new JsonObject { ["text"] = new string('y', 15_000) });

        var dispatcher = new RecordingOperationDispatcher();
        dispatcher.Responses["ansight_scan_screen"] = RequestResult.ToolResult(new JsonObject
        {
            ["content"] = content,
            ["totalCount"] = 101,
            ["truncated"] = false,
            ["screenshotFrameId"] = "ocr-frame",
            ["evidenceSource"] = "screenshotOcr"
        }, isError: false);

        var gateway = new ToolGateway(dispatcher);

        var response = await gateway.ExecuteAsync("ansight_scan_screen", new JsonObject(), "session", "scan", default);

        Assert.False(response.IsError);
        Assert.True(response.ModelOutput!.Length <= 12_000);

        var model = JsonNode.Parse(response.ModelOutput)!["result"]!;
        var entries = model["content"]!.AsArray();
        Assert.NotEmpty(entries);
        Assert.True(model["truncated"]!.GetValue<bool>());
        Assert.Equal(101, model["totalCount"]!.GetValue<int>());
        Assert.Equal(101 - entries.Count, model["projectionOmittedCount"]!.GetValue<int>());
        Assert.Equal("ocr-frame", model["screenshotFrameId"]!.GetValue<string>());
        Assert.Equal(content[1]!["text"]!.GetValue<string>(), entries[0]!["text"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(content[1]!["tapHint"], entries[0]!["tapHint"]));
        Assert.Equal(101, JsonNode.Parse(response.Output)!["result"]!["content"]!.AsArray().Count);
        Assert.Contains(new string('y', 15_000), response.Output);
    }
}
