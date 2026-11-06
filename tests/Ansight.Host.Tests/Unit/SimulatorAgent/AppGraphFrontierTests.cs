using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class AppGraphFrontierTests
{
    [Fact]
    public void UnattemptedCurrentAndUntouchedBranchesPrecedeRepeatedOrBlockedWork()
    {
        var now = DateTimeOffset.UtcNow;

        SimulatorAgentAppGraphLiveAction Action(string id, string destination, int attempts = 0,
            string tool = "ansight_tap_ui", string status = "queued", string navigationKind = "")
        {
            return new(id, destination, tool, id, new JsonObject { ["automationId"] = id }, id,
                status, attempts, "", null, now, now, navigationKind);
        }

        SimulatorAgentAppGraphLiveNode Node(string id, string scrollStatus)
        {
            return new(id, "screen", id, null, [], "", "", scrollStatus, 1m, now, now);
        }

        var actions = new[]
        {
            Action("old", "settings", 3),
            Action("settings-new", "settings"),
            Action("map-new", "map"),
            Action("current-new", "profile"),
            Action("blocked-scroll", "sheet", tool: "ansight_scroll_ui"),
            Action("unsafe", "map", status: "unsafe"),
            Action("back", "profile", navigationKind: "back")
        };
        var nodes = new[] { Node("map", "unknown"), Node("profile", "unknown"), Node("settings", "complete"), Node("sheet", "blocked") };

        var ordered = AppGraphFrontier.Pending(actions, nodes, "profile");

        Assert.Equal(["current-new", "map-new", "settings-new", "old", "blocked-scroll"], ordered.Select(action => action.Id));
    }
}
