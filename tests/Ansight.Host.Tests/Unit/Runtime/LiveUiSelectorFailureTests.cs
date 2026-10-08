namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiSelectorFailureTests
{
    [Fact]
    public void SelectorError_ContainsEffectiveSelectorAndPreservesOcrEvidence()
    {
        var selector = LiveUiSelector.Parse(new JsonObject
        {
            ["automationId"] = "save-button",
            ["ancestorAutomationId"] = "dialog",
            ["role"] = "button",
            ["visible"] = true,
            ["enabled"] = false,
            ["index"] = 2,
            ["caseSensitive"] = true
        });
        var evidence = new JsonObject { ["source"] = "ocr" };
        var result = LiveUiActionTool.BuildSelectorError("No live UI node matched the supplied selector.", selector, evidence);
        var payload = result.Payload!["structuredContent"]!;
        Assert.True(result.Payload["isError"]!.GetValue<bool>());
        Assert.False(payload["performed"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(selector.ToJson(), payload["selector"]));
        Assert.True(JsonNode.DeepEquals(evidence, payload[LiveUiOcrTraceEvidence.PayloadPropertyName]));
        var message = payload["message"]!.GetValue<string>();
        Assert.StartsWith("No live UI node matched", message);
        Assert.Contains("\"automationId\":\"save-button\"", message);
        Assert.Contains("\"ancestorAutomationId\":\"dialog\"", message);
        Assert.Contains("\"enabled\":false", message);
        Assert.Contains("\"index\":2", message);
        Assert.Contains("\"caseSensitive\":true", message);
        Assert.DoesNotContain(":null", message);
    }

    [Fact]
    public void DescribeFailure_PreservesTextEscapesAndOmitsUnspecifiedIndex()
    {
        var selector = LiveUiSelector.Parse(new JsonObject { ["text"] = "Save\nchanges" });
        var message = selector.DescribeFailure("Timed out.");
        Assert.Contains("Save\\nchanges", message);
        Assert.DoesNotContain("index", message);
        Assert.Equal("Timed out.", LiveUiSelector.Parse(null).DescribeFailure("Timed out."));
    }
}
