using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class ClientVisualTreeEventParserTests
{
    [Fact]
    public void ParseText_CreatesScreenshotCorrelatedVisualTreeSnapshot()
    {
        const string capturedAtUtc = "2026-08-10T00:15:30.123Z";
        var payload = $$"""
            {
              "type": "CLIENT_VISUAL_TREE",
              "snapshotId": "stream-test",
              "capturedAtUtc": "{{capturedAtUtc}}",
              "screenshotCapturedAtUtc": "{{capturedAtUtc}}",
              "visualTreeKind": "native",
              "visualTreeFormat": "ansight.native.visual-tree.compact.v2",
              "runtimePlatform": "ios",
              "source": "sdk.sessionCapture",
              "maxDepth": 40,
              "includeProperties": true,
              "nodeCount": 2,
              "truncated": false,
              "payload": {
                "source": "native",
                "root": {
                  "id": "root",
                  "automationId": "checkout.root",
                  "children": []
                }
              }
            }
            """;

        var parsed = ClientEventParser.ParseText(payload);

        Assert.Equal(WebSocketClientEventConstants.ClientVisualTree, parsed.Type);
        var snapshot = Assert.IsType<SessionVisualTreeSnapshot>(parsed.VisualTreeSnapshot);
        Assert.Equal("stream-test", snapshot.SnapshotId);
        Assert.Equal(DateTimeOffset.Parse(capturedAtUtc), snapshot.CapturedAtUtc);
        Assert.Equal(snapshot.CapturedAtUtc, snapshot.ScreenshotCapturedAtUtc);
        Assert.Equal("ansight.native.visual-tree.compact.v2", snapshot.VisualTreeFormat);
        Assert.Equal("ios", snapshot.RuntimePlatform);
        Assert.Equal(2, snapshot.NodeCount);
        Assert.Equal("checkout.root", snapshot.Payload["root"]?["automationId"]?.GetValue<string>());
    }

    [Fact]
    public void ParseText_PreservesMissingScreenshotCorrelation()
    {
        const string payload = """
            {
              "type": "CLIENT_VISUAL_TREE",
              "snapshotId": "stream-touch-test",
              "capturedAtUtc": "2026-08-13T00:15:30.123Z",
              "visualTreeKind": "native",
              "visualTreeFormat": "ansight.native.visual-tree.compact.v2",
              "runtimePlatform": "android",
              "source": "sdk.touchCapture",
              "nodeCount": 1,
              "payload": {
                "source": "native",
                "captureTrigger": {
                  "kind": "touch",
                  "gestureId": "gesture-test",
                  "gesturePhase": "started",
                  "touchAction": "down"
                },
                "root": {
                  "id": "root",
                  "children": []
                }
              }
            }
            """;

        var parsed = ClientEventParser.ParseText(payload);

        var snapshot = Assert.IsType<SessionVisualTreeSnapshot>(parsed.VisualTreeSnapshot);
        Assert.Null(snapshot.ScreenshotCapturedAtUtc);
        Assert.Equal("sdk.touchCapture", snapshot.Source);
        Assert.Equal("gesture-test", snapshot.Payload["captureTrigger"]?["gestureId"]?.GetValue<string>());
    }

    [Fact]
    public void ParseText_AcceptsDeepAndroidNativeVisualTree()
    {
        const int visualTreeDepth = 34;
        var root = new JsonObject
        {
            ["id"] = "node-0",
            ["children"] = new JsonArray()
        };
        var current = root;
        for (var depth = 1; depth < visualTreeDepth; depth++)
        {
            var child = new JsonObject
            {
                ["id"] = $"node-{depth}",
                ["children"] = new JsonArray()
            };
            current["children"]!.AsArray().Add(child);
            current = child;
        }

        var payload = new JsonObject
        {
            ["type"] = WebSocketClientEventConstants.ClientVisualTree,
            ["snapshotId"] = "stream-android-deep-tree",
            ["capturedAtUtc"] = "2026-08-19T11:02:45.279196Z",
            ["visualTreeKind"] = "native",
            ["visualTreeFormat"] = "ansight.native.visual-tree.compact.v2",
            ["runtimePlatform"] = "android",
            ["source"] = "sdk.touchCapture",
            ["maxDepth"] = 40,
            ["includeProperties"] = true,
            ["nodeCount"] = visualTreeDepth,
            ["truncated"] = false,
            ["payload"] = new JsonObject
            {
                ["platform"] = "android",
                ["adapter"] = "android.views",
                ["root"] = root,
                ["nodeCount"] = visualTreeDepth
            }
        };

        var parsed = ClientEventParser.ParseText(payload.ToJsonString(JsonUtil.Compact));

        Assert.Equal(WebSocketClientEventConstants.ClientVisualTree, parsed.Type);
        var snapshot = Assert.IsType<SessionVisualTreeSnapshot>(parsed.VisualTreeSnapshot);
        Assert.Equal("android", snapshot.RuntimePlatform);
        Assert.Equal(visualTreeDepth, snapshot.NodeCount);
        Assert.Contains(
            $"node-{visualTreeDepth - 1}",
            JsonSerializer.Serialize(snapshot, JsonUtil.Compact),
            StringComparison.Ordinal);
    }
}
