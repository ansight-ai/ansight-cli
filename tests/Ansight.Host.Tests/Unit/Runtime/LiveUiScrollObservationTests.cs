using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiScrollObservationTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing-child", false)]
    [InlineData("unknown-child-count", false)]
    [InlineData("truncated-descendant", false)]
    public void WindowTruncationDoesNotDiscardAProvenCompleteTargetSubtree(string scenario, bool expected)
    {
        var root = CreateContainer();
        root["childCount"] = 2;
        foreach (var child in root["children"]!.AsArray())
        {
            child!["childCount"] = 0;
        }

        var first = root["children"]![0]!;
        if (scenario == "missing-child")
        {
            first["childCount"] = 1;
        }
        else if (scenario == "unknown-child-count")
        {
            first.AsObject().Remove("childCount");
        }
        else if (scenario == "truncated-descendant")
        {
            first["truncated"] = true;
        }

        var payload = new JsonObject { ["truncated"] = true };
        Assert.Equal(expected, LiveUiScrollObservation.IsCompleteArea(root, payload, VisualTreeTypeRegistry.FromPayload(payload)));
    }

    [Fact]
    public void CaptureMetadataRuntimeIdsAndOffscreenContentDoNotProveMovement()
    {
        var before = CreateContainer();
        var after = before.DeepClone().AsObject();
        after["capturedAtUtc"] = DateTimeOffset.UtcNow;
        after["id"] = "new-container-runtime-id";
        after["children"]![0]!["id"] = "new-child-runtime-id";
        after["children"]![1]!["text"] = "Updated offscreen content";

        var observation = Compare(before, after);

        Assert.Equal("unchanged", observation["status"]!.GetValue<string>());
        Assert.Equal(1, observation["beforeVisibleNodeCount"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("position")]
    [InlineData("newly_visible")]
    public void VisibleContentAndGeometryChangesProveMovement(string change)
    {
        var before = CreateContainer();
        var after = before.DeepClone().AsObject();
        if (change == "text")
        {
            after["children"]![0]!["text"] = "Next route";
        }
        else if (change == "position")
        {
            after["children"]![0]!["bounds"]!["y"] = 15;
        }
        else
        {
            after["children"]![1]!["bounds"]!["y"] = 40;
        }

        Assert.Equal("changed", Compare(before, after)["status"]!.GetValue<string>());
    }

    [Fact]
    public void MissingSemanticContentDoesNotProveAnEndBoundary()
    {
        var before = CreateContainer();
        before["children"] = new JsonArray();
        Assert.Equal("unknown", Compare(before, before)["status"]!.GetValue<string>());
    }

    private static JsonObject Compare(JsonObject before, JsonObject after)
    {
        var types = VisualTreeTypeRegistry.FromPayload(new JsonObject());
        var viewport = new LiveUiBounds(0, 0, 100, 100);
        return LiveUiScrollObservation.CompareVisibleContent(before, types, viewport, after, types, viewport);
    }

    private static JsonObject CreateContainer()
    {
        return new JsonObject
        {
            ["id"] = "container", ["role"] = "scrollview",
            ["bounds"] = new JsonObject { ["x"] = 0, ["y"] = 10, ["width"] = 100, ["height"] = 80 },
            ["children"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "child", ["text"] = "Route", ["role"] = "text",
                    ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 20, ["width"] = 60, ["height"] = 10 }
                },
                new JsonObject
                {
                    ["text"] = "Below viewport", ["role"] = "text",
                    ["bounds"] = new JsonObject { ["x"] = 10, ["y"] = 120, ["width"] = 60, ["height"] = 10 }
                })
        };
    }
}
