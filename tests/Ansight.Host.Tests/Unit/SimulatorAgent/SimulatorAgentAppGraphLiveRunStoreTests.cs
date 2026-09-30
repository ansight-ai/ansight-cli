using System.Text.Json.Nodes;
using Ansight.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class SimulatorAgentAppGraphLiveRunStoreTests
{
    [Fact]
    public void ApplyReport_DerivesCoverageFromConcreteFrontierAndTerminalizesTransitionAction()
    {
        var store = new AppGraphLiveRunStore();
        var startedUtc = DateTimeOffset.Parse("2026-08-28T01:00:00Z");
        store.Begin("run-1", "session-1", "com.example.app", "Default", startedUtc);

        var update = store.ApplyReport(
            "run-1",
            CreateReport(
                actions:
                [
                    CreateAction("action-open-profile", "map", "open-profile", "queued")
                ]),
            1);

        Assert.True(update.IsSuccess);
        var pending = Assert.Single(update.PendingActions);
        Assert.Equal("action-open-profile", pending.Id);
        var run = Assert.IsType<SimulatorAgentAppGraphLiveRun>(store.Get("run-1"));
        Assert.Equal(1, run.Coverage.SafeActionsObserved);
        Assert.Equal(0, run.Coverage.ActionsExplored);
        Assert.Equal(2, run.Coverage.ScrollContainersObserved);
        Assert.Equal(1, run.Coverage.ScrollContainersCompleted);

        store.RecordActionAttempt(
            "run-1",
            "map",
            "ansight_tap_ui",
            new JsonObject { ["automationId"] = "open-profile" },
            succeeded: true,
            "Tap delivered.");
        var transitionUpdate = store.ApplyReport(
            "run-1",
            CreateReport(
                transitions:
                [
                    new JsonObject
                    {
                        ["id"] = "edge-open-profile",
                        ["from"] = "map",
                        ["to"] = "profile",
                        ["automationId"] = "open-profile",
                        ["semanticMeaning"] = "Open Profile",
                        ["confidence"] = 0.98
                    }
                ]),
            2);

        Assert.True(transitionUpdate.IsSuccess);
        Assert.Empty(transitionUpdate.PendingActions);
        run = Assert.IsType<SimulatorAgentAppGraphLiveRun>(store.Get("run-1"));
        var action = Assert.Single(run.Frontier);
        Assert.Equal("explored", action.Status);
        Assert.Equal("profile", action.ResultDestinationId);
        Assert.Equal(1, action.AttemptCount);
        Assert.Equal(1, run.Coverage.ActionsExplored);
        Assert.True(store.TryBuildActionGuardMessage(
            "run-1",
            "map",
            "ansight_tap_ui",
            "open-profile",
            2,
            out var guardMessage));
        Assert.Contains("already terminal", guardMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReport_RejectsConflictingCanonicalTransition()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run-1", "session-1", "com.example.app", "Default", DateTimeOffset.UtcNow);
        var first = store.ApplyReport(
            "run-1",
            CreateReport(
                transitions:
                [
                    CreateTransition("edge-map-filters", "dialog-map-filters")
                ]),
            1);

        var conflicting = store.ApplyReport(
            "run-1",
            CreateReport(
                transitions:
                [
                    CreateTransition("edge-area-filters", "dialog-area-filters")
                ]),
            2);

        Assert.True(first.IsSuccess);
        Assert.False(conflicting.IsSuccess);
        Assert.Contains("already leads to", conflicting.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyReport_GroupsNavigationHostChildrenUnderHostDestination()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run-1", "session-1", "com.example.app", "Default", DateTimeOffset.UtcNow);

        var update = store.ApplyReport(
            "run-1",
            CreateReport(
                destinations:
                [
                    CreateDestination("main-host", "Main navigation", "not_scrollable"),
                    CreateDestination("map", "Map", "not_scrollable"),
                    CreateDestination("profile", "Profile", "not_scrollable")
                ],
                navigationHosts:
                [
                    new JsonObject
                    {
                        ["id"] = "host-main",
                        ["kind"] = "bottom_tabs",
                        ["name"] = "Main navigation",
                        ["destinationId"] = "main-host",
                        ["activeChildDestinationId"] = "map",
                        ["childDestinationIds"] = new JsonArray("map", "profile"),
                        ["framework"] = "maui",
                        ["technology"] = new JsonObject
                        {
                            ["framework"] = "maui",
                            ["kind"] = "shell_tab_bar",
                            ["navigationToolId"] = "maui.get_navigation_state",
                            ["structureFingerprint"] = new string('a', 64)
                        },
                        ["confidence"] = 0.98
                    }
                ]),
            1);

        Assert.True(update.IsSuccess);
        var run = Assert.IsType<SimulatorAgentAppGraphLiveRun>(store.Get("run-1"));
        Assert.Equal("main-host", run.Nodes.Single(node => node.Id == "map").ParentScreen);
        Assert.Equal("main-host", run.Nodes.Single(node => node.Id == "profile").ParentScreen);
        Assert.Equal("shell_tab_bar", Assert.Single(run.NavigationHosts).Technology?.Kind);
        Assert.Empty(store.GetStructureGaps("run-1"));
    }

    [Fact]
    public void GetStructureGaps_RequiresInitiallySelectedTabAndSiblingInventory()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run-1", "session-1", "com.example.app", "Default", DateTimeOffset.UtcNow);

        var update = store.ApplyReport(
            "run-1",
            CreateReport(
                destinations:
                [
                    CreateDestination("profile", "Profile", "not_scrollable"),
                    CreateDestination("activity", "Activity", "not_scrollable", "state", "profile")
                ],
                tabGroups:
                [
                    new JsonObject
                    {
                        ["id"] = "profile-tabs",
                        ["parentDestinationId"] = "profile",
                        ["selectedDestinationId"] = "activity",
                        ["tabDestinationIds"] = new JsonArray("activity"),
                        ["confidence"] = 0.9
                    }
                ]),
            1);

        Assert.True(update.IsSuccess);
        var gap = Assert.Single(store.GetStructureGaps("run-1"));
        Assert.Contains("initially selected tab", gap, StringComparison.Ordinal);
    }

    [Fact]
    public void Database_PersistsNodesEdgesAndActionFrontier()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "ansight-app-graph-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var database = new AppGraphExplorationDatabase(
                new DataToolApplicationPaths(rootPath));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("run-db", "session-1", "com.example.app", "Default", DateTimeOffset.UtcNow);
            var update = store.ApplyReport(
                "run-db",
                CreateReport(
                    transitions:
                    [
                        CreateTransition("edge-map-filters", "dialog-map-filters")
                    ],
                    actions:
                    [
                        CreateAction("action-open-profile", "map", "open-profile", "queued")
                    ],
                    destinations:
                    [
                        CreateDestination("main-host", "Main navigation", "not_scrollable"),
                        CreateDestination("map", "Map", "not_scrollable"),
                        CreateDestination("profile", "Profile", "not_scrollable"),
                        CreateDestination("overview", "Overview", "not_scrollable", "state", "profile"),
                        CreateDestination("activity", "Activity", "not_scrollable", "state", "profile")
                    ],
                    navigationHosts:
                    [
                        new JsonObject
                        {
                            ["id"] = "host-main",
                            ["kind"] = "bottom_tabs",
                            ["name"] = "Main navigation",
                            ["destinationId"] = "main-host",
                            ["activeChildDestinationId"] = "map",
                            ["childDestinationIds"] = new JsonArray("map", "profile"),
                            ["framework"] = "maui",
                            ["technology"] = CreateTechnology("shell_tab_bar"),
                            ["confidence"] = 0.95
                        }
                    ],
                    tabGroups:
                    [
                        new JsonObject
                        {
                            ["id"] = "profile-tabs",
                            ["parentDestinationId"] = "profile",
                            ["selectedDestinationId"] = "overview",
                            ["tabDestinationIds"] = new JsonArray("overview", "activity"),
                            ["technology"] = CreateTechnology("tabbed_page"),
                            ["confidence"] = 0.9
                        }
                    ]),
                1);

            Assert.True(update.IsSuccess);
            using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
            connection.Open();
            Assert.Equal(5L, ReadCount(connection, "app_graph_nodes"));
            Assert.Equal(1L, ReadCount(connection, "app_graph_edges"));
            Assert.Equal(1L, ReadCount(connection, "app_graph_action_candidates"));
            Assert.Equal(1L, ReadCount(connection, "app_graph_navigation_hosts"));
            Assert.Equal(1L, ReadCount(connection, "app_graph_tab_groups"));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM app_graph_action_candidates WHERE action_id = 'action-open-profile';";
            Assert.Equal("queued", command.ExecuteScalar());
            connection.Close();

            var resumedStore = new AppGraphLiveRunStore(database);
            resumedStore.Begin(
                "run-resumed",
                "session-1",
                "com.example.app",
                "Default",
                DateTimeOffset.UtcNow.AddMinutes(1));
            var resumed = Assert.IsType<SimulatorAgentAppGraphLiveRun>(
                resumedStore.Get("run-resumed"));
            Assert.Equal(5, resumed.Nodes.Count);
            Assert.Single(resumed.Edges);
            Assert.Single(resumed.NavigationHosts);
            Assert.Single(resumed.TabGroups);
            Assert.Equal("shell_tab_bar", resumed.NavigationHosts[0].Technology?.Kind);
            Assert.Equal("tabbed_page", resumed.TabGroups[0].Technology?.Kind);
            Assert.Equal("action-open-profile", Assert.Single(resumed.Frontier).Id);
            Assert.Contains("Resumed the incomplete frontier", resumed.Message, StringComparison.Ordinal);

            resumedStore.Complete(
                "run-resumed",
                "succeeded",
                "Exploration completed.",
                DateTimeOffset.UtcNow.AddMinutes(2));
            var freshStore = new AppGraphLiveRunStore(database);
            freshStore.Begin(
                "run-fresh",
                "session-1",
                "com.example.app",
                "Default",
                DateTimeOffset.UtcNow.AddMinutes(3));
            var fresh = Assert.IsType<SimulatorAgentAppGraphLiveRun>(freshStore.Get("run-fresh"));
            Assert.Empty(fresh.Nodes);
            Assert.Empty(fresh.Edges);
            Assert.Empty(fresh.Frontier);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static JsonObject CreateReport(
        IReadOnlyList<JsonObject>? transitions = null,
        IReadOnlyList<JsonObject>? actions = null,
        IReadOnlyList<JsonObject>? destinations = null,
        IReadOnlyList<JsonObject>? navigationHosts = null,
        IReadOnlyList<JsonObject>? tabGroups = null)
        => new()
        {
            ["message"] = "Updated host frontier.",
            ["currentDestinationId"] = "map",
            ["destinations"] = new JsonArray(
                (destinations ??
                [
                    CreateDestination("map", "Map", "in_progress"),
                    CreateDestination("profile", "Profile", "not_scrollable")
                ]).Select(static value => (JsonNode?)value).ToArray()),
            ["transitions"] = new JsonArray(
                (transitions ?? []).Select(static value => (JsonNode?)value).ToArray()),
            ["navigationHosts"] = new JsonArray(
                (navigationHosts ?? []).Select(static value => (JsonNode?)value).ToArray()),
            ["tabGroups"] = new JsonArray(
                (tabGroups ?? []).Select(static value => (JsonNode?)value).ToArray()),
            ["actions"] = new JsonArray(
                (actions ?? []).Select(static value => (JsonNode?)value).ToArray()),
            ["coverage"] = new JsonObject
            {
                ["safeActionsObserved"] = 99,
                ["actionsExplored"] = 0,
                ["scrollContainersObserved"] = 1,
                ["scrollContainersCompleted"] = 5,
                ["gaps"] = new JsonArray()
            }
        };

    private static JsonObject CreateDestination(
        string id,
        string name,
        string scrollStatus,
        string kind = "screen",
        string parentScreen = "")
        => new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = name,
            ["parentScreen"] = parentScreen,
            ["synonyms"] = new JsonArray(),
            ["purpose"] = $"Use {name}.",
            ["description"] = $"{name} is visible.",
            ["scrollStatus"] = scrollStatus,
            ["confidence"] = 0.95
        };

    private static JsonObject CreateTechnology(string kind)
        => new()
        {
            ["framework"] = "maui",
            ["kind"] = kind,
            ["navigationToolId"] = "maui.get_navigation_state",
            ["structureFingerprint"] = new string('a', 64)
        };

    private static JsonObject CreateAction(
        string id,
        string destinationId,
        string automationId,
        string status)
        => new()
        {
            ["id"] = id,
            ["destinationId"] = destinationId,
            ["toolName"] = "ansight_tap_ui",
            ["automationId"] = automationId,
            ["semanticMeaning"] = "Open Profile",
            ["status"] = status,
            ["lastOutcome"] = string.Empty,
            ["resultDestinationId"] = string.Empty
        };

    private static JsonObject CreateTransition(string id, string destinationId)
        => new()
        {
            ["id"] = id,
            ["from"] = "map",
            ["to"] = destinationId,
            ["automationId"] = "map-open-filters-button",
            ["semanticMeaning"] = "Open map filters",
            ["confidence"] = 0.95
        };

    private static long ReadCount(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
