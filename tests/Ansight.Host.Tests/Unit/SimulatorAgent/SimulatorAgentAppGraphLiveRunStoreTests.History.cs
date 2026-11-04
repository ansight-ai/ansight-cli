using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentAppGraphLiveRunStoreTests
{
    [Theory]
    [InlineData("failed", "failed")]
    [InlineData("partial", "partial")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("succeeded", "succeeded")]
    [InlineData("starting", "partial")]
    [InlineData("running", "partial")]
    public void ViewerListsPersistedRunsAfterRestartWithoutStartingExploration(
        string storedStatus, string expectedStatus)
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-graph-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DataToolApplicationPaths(root);
            var database = new AppGraphExplorationDatabase(paths);
            var liveStore = new AppGraphLiveRunStore(database);
            liveStore.Begin("retained", "recording", "app", "Default", DateTimeOffset.UtcNow);
            Assert.True(liveStore.ApplyReport("retained", CreateReport(
                transitions: [CreateTransition("map-profile", "profile")],
                actions: [CreateAction("open-profile", "map", "open-profile", "queued")]), 1).IsSuccess);
            var pending = liveStore.RecordDeliveredInput("retained", "map", "ansight_tap_ui",
                new JsonObject { ["automationId"] = "open-profile" }, "input", "evidence", "Delivered.");
            Assert.NotNull(pending);
            var original = liveStore.Get("retained")!;
            database.Save(original with { Status = storedStatus });

            var restarted = new AppGraphLiveRunStore(new AppGraphExplorationDatabase(paths));
            var retained = Assert.Single(restarted.List("recording"));
            Assert.Equal("retained", retained.RunId);
            Assert.Equal(expectedStatus, retained.Status);
            Assert.Equal(original.Nodes.Select(node => node.Id), retained.Nodes.Select(node => node.Id));
            Assert.Equal(original.Nodes.Select(node => node.ScrollStatus), retained.Nodes.Select(node => node.ScrollStatus));
            Assert.Equal(original.Edges.Select(edge => edge.Id), retained.Edges.Select(edge => edge.Id));
            Assert.Equal(original.Coverage.ActionsExplored, retained.Coverage.ActionsExplored);
            Assert.Equal("needs_verification", Assert.Single(retained.Frontier).Status);
            Assert.Equal(pending.InputId, retained.PendingOutcome?.InputId);
            Assert.Empty(restarted.List("another-recording"));

            liveStore.UpdateActivity("retained", "Still executing in this host.", 9, "ansight_find_ui");
            var active = Assert.Single(liveStore.List("recording"));
            Assert.Equal("running", active.Status);
            Assert.Equal(9, active.Turn);
            Assert.Equal("Still executing in this host.", active.Message);
            Assert.Equal("ansight_find_ui", active.ActiveToolName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
