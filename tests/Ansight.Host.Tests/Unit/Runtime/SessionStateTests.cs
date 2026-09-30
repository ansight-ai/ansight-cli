namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionStateTests
{
    [Fact]
    public void GetSnapshotVisualTrees_DeepClonesOnlyNewAndReplacedTrees()
    {
        var capturedAtUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var state = new SessionState
        {
            SessionId = "session-visual-tree-cache",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedAtUtc,
            LastUpdatedUtc = capturedAtUtc,
            Status = "Connected",
            ConfigId = "config-001"
        };
        var firstSource = CreateVisualTreeSnapshot("visual-tree-001", capturedAtUtc, 1);
        state.VisualTreeSnapshots.Add(firstSource);
        state.MarkVisualTreesChanged();

        var firstSnapshot = Assert.Single(state.GetSnapshotVisualTrees());

        Assert.NotSame(firstSource, firstSnapshot);
        Assert.NotSame(firstSource.Payload, firstSnapshot.Payload);

        var secondSource = CreateVisualTreeSnapshot("visual-tree-002", capturedAtUtc.AddSeconds(1), 2);
        state.VisualTreeSnapshots.Add(secondSource);
        state.MarkVisualTreesChanged();
        var appendedSnapshots = state.GetSnapshotVisualTrees();

        Assert.Same(firstSnapshot, appendedSnapshots[0]);
        Assert.NotSame(secondSource, appendedSnapshots[1]);

        state.VisualTreeSnapshots[0] = CreateVisualTreeSnapshot("visual-tree-001", capturedAtUtc, 3);
        state.MarkVisualTreesChanged();
        var replacedSnapshots = state.GetSnapshotVisualTrees();

        Assert.NotSame(appendedSnapshots[0], replacedSnapshots[0]);
        Assert.Same(appendedSnapshots[1], replacedSnapshots[1]);
        Assert.Equal(3, replacedSnapshots[0].NodeCount);
    }

    private static SessionVisualTreeSnapshot CreateVisualTreeSnapshot(
        string snapshotId,
        DateTimeOffset capturedAtUtc,
        int nodeCount)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "test",
            NodeCount = nodeCount,
            Payload = new JsonObject
            {
                ["nodeCount"] = nodeCount
            }
        };
    }
}
