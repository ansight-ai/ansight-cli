using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
    [Fact]
    public void ReceiveSessionVisualTreeSnapshot_DiscardsMatchingPreviousTree()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var first = CreateReceivedVisualTreeSnapshot(
            "tree-1",
            "2026-08-21T00:00:00Z",
            "gesture-1",
            "Continue");
        var duplicate = CreateReceivedVisualTreeSnapshot(
            "tree-2",
            "2026-08-21T00:00:01Z",
            "gesture-2",
            "Continue");

        var firstResult = runtimeState.ReceiveSessionVisualTreeSnapshot(sessionId, first);
        var duplicateResult = runtimeState.ReceiveSessionVisualTreeSnapshot(sessionId, duplicate);

        Assert.True(firstResult.WasSaved);
        Assert.True(duplicateResult.WasDiscarded);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal("tree-1", Assert.Single(snapshot!.VisualTreeSnapshots).SnapshotId);
    }

    [Fact]
    public void ReceiveSessionVisualTreeSnapshot_RetainsChangedTree()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var first = CreateReceivedVisualTreeSnapshot(
            "tree-1",
            "2026-08-21T00:00:00Z",
            "gesture-1",
            "Continue");
        var changed = CreateReceivedVisualTreeSnapshot(
            "tree-2",
            "2026-08-21T00:00:01Z",
            "gesture-2",
            "Done");

        var firstResult = runtimeState.ReceiveSessionVisualTreeSnapshot(sessionId, first);
        var changedResult = runtimeState.ReceiveSessionVisualTreeSnapshot(sessionId, changed);

        Assert.True(firstResult.WasSaved);
        Assert.True(changedResult.WasSaved);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal(["tree-1", "tree-2"], snapshot!.VisualTreeSnapshots.Select(tree => tree.SnapshotId).ToArray());
    }

    private static SessionVisualTreeSnapshot CreateReceivedVisualTreeSnapshot(
        string snapshotId,
        string capturedAtUtc,
        string gestureId,
        string label)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = DateTimeOffset.Parse(capturedAtUtc),
            VisualTreeKind = "native",
            VisualTreeFormat = "ansight.native.visual-tree.compact.v2",
            RuntimePlatform = "ios",
            Source = "sdk.touchCapture",
            RootScope = "window",
            MaxDepth = 40,
            IncludeProperties = true,
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["capturedAtUtc"] = capturedAtUtc,
                ["source"] = "native",
                ["captureTrigger"] = new JsonObject
                {
                    ["kind"] = "touch",
                    ["gestureId"] = gestureId
                },
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["label"] = label,
                    ["children"] = new JsonArray()
                }
            }
        };
    }
}
