using System.Text.Json.Nodes;
using Ansight.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentAppGraphLiveRunStoreTests
{
    [Theory]
    [InlineData("header-back-button", "")]
    [InlineData("popPage", "")]
    [InlineData("sheet-dismiss", "")]
    [InlineData("custom-return-control", "pop")]
    [InlineData("for-you-toggle-home-flyout-button", "")]
    [InlineData("home-tab-crags", "")]
    [InlineData("area-tabs-toolbar-beta-tab", "")]
    [InlineData("custom-menu-control", "menu")]
    [InlineData("custom-tab-control", "tab")]
    public void NavigationEssentialsRemainReusableAfterVerifiedTransitions(string automationId, string navigationKind)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("nav", "session", "app", "Default", DateTimeOffset.UtcNow);
        var action = CreateAction("return", "map", automationId, "queued");
        action["navigationKind"] = navigationKind;
        Assert.True(store.ApplyReport("nav", CreateReport(actions: [action]), 1).IsSuccess);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(store.TryBuildActionGuardMessage("nav", "map", "ansight_tap_ui", automationId, 2, out _));
            store.RecordActionAttempt("nav", "map", "ansight_tap_ui",
                new JsonObject { ["automationId"] = automationId }, true, "Returned.");
            var transition = CreateTransition("return-edge", "profile");
            transition["automationId"] = automationId;
            Assert.True(store.ApplyReport("nav", CreateReport(transitions: [transition]), attempt + 2).IsSuccess);
        }
        var run = store.Get("nav")!;
        Assert.Equal("navigation", Assert.Single(run.Frontier).Status);
        Assert.Equal(5, run.Frontier[0].AttemptCount);
        Assert.Equal(0, run.Coverage.ActionsExplored);
        Assert.Equal(0, run.Coverage.SafeActionsObserved);
    }

    [Fact]
    public void NavigationWordsInContentDoNotExemptOrdinaryActions()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("content", "session", "app", "Default", DateTimeOffset.UtcNow);
        var action = CreateAction("route", "map", "", "explored");
        var selector = new JsonObject { ["text"] = "Back in Black", ["role"] = "text" };
        action["selector"] = selector.DeepClone();
        Assert.True(store.ApplyReport("content", CreateReport(actions: [action]), 1).IsSuccess);
        Assert.True(store.TryBuildActionGuardMessage("content", "map", "ansight_tap_ui", "", 2, out _, selector));
        Assert.False(Assert.Single(store.Get("content")!.Frontier).IsNavigationEssential);
    }

    [Fact]
    public void ObservedSelectorsStayQueuedAndDoNotShareAttemptCounts()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("observed", "session", "app", "Default", DateTimeOffset.UtcNow);
        var account = CreateAction("account", "map", "", "queued");
        account["selector"] = new JsonObject { ["automationId"] = "", ["text"] = "Account", ["role"] = "text" };
        var settings = CreateAction("settings", "map", "", "queued");
        settings["selector"] = new JsonObject { ["text"] = "Settings", ["role"] = "text" };
        var report = store.ApplyReport("observed", CreateReport(actions: [account, settings]), 1);
        Assert.True(report.IsSuccess, report.Message);
        Assert.Equal(2, report.PendingActions.Count);
        store.RecordActionAttempt("observed", "map", "ansight_tap_ui",
            new JsonObject { ["text"] = "Account", ["role"] = "text", ["exact"] = true }, true, "Opened account.");
        var run = store.Get("observed")!;
        Assert.Equal(2, run.Frontier.Count);
        Assert.Equal(1, run.Frontier.Single(action => action.Id == "account").AttemptCount);
        Assert.Equal(0, run.Frontier.Single(action => action.Id == "settings").AttemptCount);
        Assert.All(run.Frontier, action => Assert.False(action.HasStableAutomationId));
        var edge = CreateTransition("account-edge", "profile");
        edge["automationId"] = "";
        edge["selector"] = new JsonObject { ["text"] = "Account", ["role"] = "text" };
        var completed = store.ApplyReport("observed", CreateReport(transitions: [edge]), 2);
        Assert.True(completed.IsSuccess);
        Assert.Equal("settings", Assert.Single(completed.PendingActions).Id);
        Assert.False(Assert.Single(store.Get("observed")!.Edges).HasStableAutomationId);
    }

    [Fact]
    public void RepeatedLabelsKeepSeparateObservedMatchIndexes()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("indexes", "session", "app", "Default", DateTimeOffset.UtcNow);
        var first = CreateAction("first", "map", "", "explored");
        first["selector"] = new JsonObject { ["text"] = "Details", ["index"] = 0 };
        var second = CreateAction("second", "map", "", "queued");
        var secondSelector = new JsonObject { ["text"] = "Details", ["index"] = 1 };
        second["selector"] = secondSelector.DeepClone();
        Assert.True(store.ApplyReport("indexes", CreateReport(actions: [first, second]), 1).IsSuccess);
        Assert.True(store.TryBuildActionGuardMessage("indexes", "map", "ansight_tap_ui", "", 2,
            out _, new JsonObject { ["text"] = "Details" }));
        Assert.False(store.TryBuildActionGuardMessage("indexes", "map", "ansight_tap_ui", "", 2, out _, secondSelector));
        store.RecordActionAttempt("indexes", "map", "ansight_tap_ui", secondSelector, true, "Opened the second item.");
        var run = store.Get("indexes")!;
        Assert.Equal(2, run.Frontier.Count);
        Assert.Equal(0, run.Frontier.Single(action => action.Id == "first").AttemptCount);
        Assert.Equal(1, run.Frontier.Single(action => action.Id == "second").AttemptCount);
    }

    [Fact]
    public void DatabaseRestoresNavigationRoleAndObservedSelectorEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-graph-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new AppGraphExplorationDatabase(new DataToolApplicationPaths(root));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("first", "session", "app", "Default", DateTimeOffset.UtcNow);
            var action = CreateAction("return", "map", "", "explored");
            action["navigationKind"] = "pop";
            action["selector"] = new JsonObject { ["text"] = "Previous" };
            var edge = CreateTransition("return-edge", "profile");
            edge["automationId"] = "";
            edge["selector"] = new JsonObject { ["text"] = "Previous" };
            Assert.True(store.ApplyReport("first", CreateReport(actions: [action], transitions: [edge]), 1).IsSuccess);
            var resumed = new AppGraphLiveRunStore(new AppGraphExplorationDatabase(new DataToolApplicationPaths(root)));
            resumed.Begin("second", "session", "app", "Default", DateTimeOffset.UtcNow);
            var run = resumed.Get("second")!;
            Assert.Equal("navigation", Assert.Single(run.Frontier).Status);
            Assert.Equal("pop", run.Frontier[0].NavigationKind);
            Assert.Equal("Previous", Assert.Single(run.Edges).Selector!["text"]!.GetValue<string>());
            Assert.Equal("pop", run.Edges[0].NavigationKind);
            Assert.Equal("profile", run.Edges[0].NavigationContext!.ReturnDestinationId);
        }
        finally { Directory.Delete(root, true); }
    }

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
        Assert.False(store.TryBuildActionGuardMessage(
            "run-1",
            "map",
            "ansight_tap_ui",
            "open-profile",
            2,
            out var guardMessage));
        Assert.Empty(guardMessage);
        for (var attempt = 0; attempt < 4; attempt++)
            store.RecordActionAttempt("run-1", "map", "ansight_tap_ui",
                new JsonObject { ["automationId"] = "open-profile" }, true, "Traversed known route.");
        Assert.False(store.TryBuildActionGuardMessage("run-1", "map", "ansight_tap_ui", "open-profile", 2, out _));
        Assert.Equal("explored", Assert.Single(store.Get("run-1")!.Frontier).Status);
        Assert.Equal(1, store.Get("run-1")!.Coverage.ActionsExplored);
    }

    [Theory]
    [InlineData("unsafe")]
    [InlineData("blocked")]
    [InlineData("unavailable")]
    public void NavigationRoleDoesNotOverrideExplicitRestrictions(string status)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("restricted", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("restricted", CreateReport(actions:
            [CreateAction("tab", "map", "home-tab-crags", status)]), 1).IsSuccess);
        Assert.True(store.TryBuildActionGuardMessage("restricted", "map", "ansight_tap_ui", "home-tab-crags", 2, out _));
    }

    [Fact]
    public void NewPassPreservesBlockersAndHistoryUntilAnExplicitEvidenceBackedRecheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-graph-recheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new AppGraphExplorationDatabase(new DataToolApplicationPaths(root));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("first", "session", "app", "Default", DateTimeOffset.UtcNow);
            Assert.True(store.ApplyReport("first", CreateReport(actions:
                [CreateAction("details", "map", "details", "blocked"),
                 CreateAction("purchase", "map", "purchase", "unsafe")]), 1).IsSuccess);
            store.RecordActionAttempt("first", "map", "ansight_tap_ui",
                new JsonObject { ["automationId"] = "details" }, false, "Route was guarded.");
            store.Complete("first", "failed", "Incomplete.", DateTimeOffset.UtcNow);
            var resumed = new AppGraphLiveRunStore(database);
            resumed.Begin("second", "new-session", "app", "Default", DateTimeOffset.UtcNow);
            var actions = resumed.Get("second")!.Frontier;
            Assert.Equal("blocked", actions.Single(action => action.Id == "details").Status);
            Assert.Equal(1, actions.Single(action => action.Id == "details").AttemptCount);
            Assert.Contains("Route was guarded", actions.Single(action => action.Id == "details").LastOutcome);
            Assert.Equal("unsafe", actions.Single(action => action.Id == "purchase").Status);
            var rediscovered = CreateAction("details", "map", "details", "queued");
            Assert.True(resumed.ApplyReport("second", CreateReport(actions: [rediscovered]), 2).IsSuccess);
            Assert.Equal("blocked", resumed.Get("second")!.Frontier.Single(action => action.Id == "details").Status);
            rediscovered["recheckReason"] = "The unavailable native capture provider was repaired and is observed again.";
            Assert.True(resumed.ApplyReport("second", CreateReport(actions: [rediscovered.DeepClone().AsObject()]), 3).IsSuccess);
            Assert.Equal("queued", resumed.Get("second")!.Frontier.Single(action => action.Id == "details").Status);
            Assert.False(resumed.TryBuildActionGuardMessage("second", "map", "ansight_tap_ui", "details", 1, out _));
            resumed.RecordActionAttempt("second", "map", "ansight_tap_ui", new JsonObject { ["automationId"] = "details" }, true, "Retried.");
            Assert.True(resumed.TryBuildActionGuardMessage("second", "map", "ansight_tap_ui", "details", 1, out _));
            Assert.Equal(2, resumed.Get("second")!.Frontier.Single(action => action.Id == "details").AttemptCount);
            var unsafeRecheck = CreateAction("purchase", "map", "purchase", "queued");
            unsafeRecheck["recheckReason"] = "Still unsafe.";
            Assert.True(resumed.ApplyReport("second", CreateReport(actions: [unsafeRecheck]), 4).IsSuccess);
            Assert.Equal("unsafe", resumed.Get("second")!.Frontier.Single(action => action.Id == "purchase").Status);
            resumed.Begin("other-app", "new-session", "other-app", "Default", DateTimeOffset.UtcNow);
            Assert.Empty(resumed.Get("other-app")!.Frontier);
            resumed.Begin("other-graph", "new-session", "app", "Other", DateTimeOffset.UtcNow);
            Assert.Empty(resumed.Get("other-graph")!.Frontier);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("attempted")]
    [InlineData("deferred")]
    [InlineData("queued")]
    [InlineData("blocked")]
    public void EvidenceBackedRecheckSurvivesResumeWithoutResettingHistoryOrRenewingTheSameReason(string status)
    {
        var root = Path.Combine(Path.GetTempPath(), "ansight-graph-recheck-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new AppGraphExplorationDatabase(new DataToolApplicationPaths(root));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("first", "session", "app", "Default", DateTimeOffset.UtcNow);
            Assert.True(store.ApplyReport("first", CreateReport(actions:
                [CreateAction("mode", "map", "mode", "queued")]), 1).IsSuccess);
            var selector = new JsonObject { ["automationId"] = "mode" };
            for (var attempt = 0; attempt < 2; attempt++)
            {
                store.RecordActionAttempt("first", "map", "ansight_tap_ui", selector, true, "Unverified old attempt.");
            }
            Assert.True(store.ApplyReport("first", CreateReport(actions:
                [CreateAction("mode", "map", "mode", status)]), 2).IsSuccess);
            Assert.True(store.TryBuildActionGuardMessage("first", "map", "ansight_tap_ui", "mode", 2, out _));
            var recheck = CreateAction("mode", "map", "mode", "queued");
            recheck["recheckReason"] = "Fresh rendered baseline is available after repairing screenshot assessment.";
            Assert.True(store.ApplyReport("first", CreateReport(actions: [recheck.DeepClone().AsObject()]), 3).IsSuccess);
            Assert.False(store.TryBuildActionGuardMessage("first", "map", "ansight_tap_ui", "mode", 2, out _));
            store.Complete("first", "partial", "Resume recheck.", DateTimeOffset.UtcNow);

            var resumed = new AppGraphLiveRunStore(database);
            resumed.Begin("second", "new-session", "app", "Default", DateTimeOffset.UtcNow);
            var action = Assert.Single(resumed.Get("second")!.Frontier);
            Assert.Equal(2, action.AttemptCount);
            Assert.Equal(2, action.RecheckAttemptBaseline);
            Assert.Equal(recheck["recheckReason"]!.GetValue<string>(), action.LastRecheckReason);
            Assert.False(resumed.TryBuildActionGuardMessage("second", "map", "ansight_tap_ui", "mode", 2, out _));
            resumed.RecordActionAttempt("second", "map", "ansight_tap_ui", selector, true, "First recheck attempt.");
            resumed.Complete("second", "partial", "Resume remaining allowance.", DateTimeOffset.UtcNow);

            var third = new AppGraphLiveRunStore(database);
            third.Begin("third", "third-session", "app", "Default", DateTimeOffset.UtcNow);
            Assert.False(third.TryBuildActionGuardMessage("third", "map", "ansight_tap_ui", "mode", 2, out _));
            third.RecordActionAttempt("third", "map", "ansight_tap_ui", selector, true, "Second recheck attempt.");
            Assert.True(third.TryBuildActionGuardMessage("third", "map", "ansight_tap_ui", "mode", 2, out _));
            Assert.True(third.ApplyReport("third", CreateReport(actions: [recheck.DeepClone().AsObject()]), 4).IsSuccess);
            Assert.True(third.TryBuildActionGuardMessage("third", "map", "ansight_tap_ui", "mode", 2, out _));
            Assert.Equal(4, Assert.Single(third.Get("third")!.Frontier).AttemptCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
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
                ], destinations:
                [
                    CreateDestination("map", "Map", "not_scrollable"),
                    CreateDestination("dialog-map-filters", "Map filters", "not_scrollable", "dialog", "map")
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
    public void Database_PreservesFlyoutOwningTabbedPageAcrossIncrementalReportsAndResume()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "ansight-nested-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        try
        {
            var database = new AppGraphExplorationDatabase(new DataToolApplicationPaths(rootPath));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("nested", "session-1", "com.example.nested", "Default", DateTimeOffset.UtcNow);
            Assert.True(store.ApplyReport("nested", CreateReport(destinations:
            [
                CreateDestination("flyout", "Home flyout", "not_scrollable"),
                CreateDestination("tabs", "Main tabs", "not_scrollable"),
                CreateDestination("map", "Map", "not_scrollable"),
                CreateDestination("crags", "Crags", "not_scrollable")
            ], navigationHosts: [CreateNestedHost("tabs", "top_tabs", "tabbed_page", "map", "crags")]), 1).IsSuccess);
            Assert.True(store.ApplyReport("nested", CreateReport(navigationHosts:
                [CreateNestedHost("flyout", "flyout", "flyout_page", "tabs")]), 2).IsSuccess);
            // Revisiting a tab must not detach it from its host.
            Assert.True(store.ApplyReport("nested", CreateReport(destinations:
                [CreateDestination("map", "Map", "not_scrollable")]), 3).IsSuccess);
            Assert.Empty(store.GetStructureGaps("nested"));
            var resumedStore = new AppGraphLiveRunStore(database);
            resumedStore.Begin("resumed", "session-1", "com.example.nested", "Default", DateTimeOffset.UtcNow.AddMinutes(1));
            var run = resumedStore.Get("resumed")!;
            Assert.Equal("flyout", run.Nodes.Single(node => node.Id == "tabs").ParentScreen);
            Assert.Equal("tabs", run.Nodes.Single(node => node.Id == "map").ParentScreen);
            Assert.Equal("tabs", run.Nodes.Single(node => node.Id == "crags").ParentScreen);
            Assert.Empty(run.Edges);
            Assert.Empty(resumedStore.GetStructureGaps("resumed"));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("self", "cannot own itself")]
    [InlineData("cycle", "cycle")]
    [InlineData("flattened", "multiple direct owners")]
    public void GetStructureGaps_ReportsInvalidNestedOwnership(string corruption, string expectedError)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("nested", "session-1", "com.example.app", "Default", DateTimeOffset.UtcNow);
        var flyout = CreateNestedHost("flyout", "flyout", "flyout_page", "tabs");
        var tabs = CreateNestedHost("tabs", "top_tabs", "tabbed_page", "map", "crags");
        if (corruption == "self") flyout["childDestinationIds"]!.AsArray().Add("flyout");
        if (corruption == "cycle") tabs["childDestinationIds"]!.AsArray().Add("flyout");
        if (corruption == "flattened") flyout["childDestinationIds"]!.AsArray().Add("map");
        Assert.True(store.ApplyReport("nested", CreateReport(destinations:
        [
            CreateDestination("flyout", "Home flyout", "not_scrollable"),
            CreateDestination("tabs", "Main tabs", "not_scrollable"),
            CreateDestination("map", "Map", "not_scrollable"),
            CreateDestination("crags", "Crags", "not_scrollable")
        ], navigationHosts: [flyout, tabs]), 1).IsSuccess);
        Assert.Contains(store.GetStructureGaps("nested"), gap => gap.Contains(expectedError, StringComparison.Ordinal));
    }

    private static JsonObject CreateNestedHost(string destinationId, string kind, string technology, params string[] children)
        => new()
        {
            ["id"] = "host-" + destinationId,
            ["kind"] = kind,
            ["name"] = destinationId,
            ["destinationId"] = destinationId,
            ["activeChildDestinationId"] = children[0],
            ["childDestinationIds"] = new JsonArray(children.Select(child => (JsonNode?)JsonValue.Create(child)).ToArray()),
            ["framework"] = "maui",
            ["technology"] = CreateTechnology(technology),
            ["confidence"] = 0.95
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetStructureGaps_RequiresSelectedTabWithoutInventingSiblings(bool includesSelectedTab)
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
                        ["selectedDestinationId"] = includesSelectedTab ? "activity" : "profile",
                        ["tabDestinationIds"] = new JsonArray("activity"),
                        ["confidence"] = 0.9
                    }
                ]),
            1);

        Assert.True(update.IsSuccess);
        if (includesSelectedTab)
        {
            Assert.Empty(store.GetStructureGaps("run-1"));
        }
        else
        {
            var gap = Assert.Single(store.GetStructureGaps("run-1"));
            Assert.Contains("does not include selected tab", gap, StringComparison.Ordinal);
        }
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
                        CreateDestination("activity", "Activity", "not_scrollable", "state", "profile"),
                        CreateDestination("dialog-map-filters", "Map filters", "not_scrollable", "dialog", "map")
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
            Assert.Equal(6L, ReadCount(connection, "app_graph_nodes"));
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
            Assert.Equal(6, resumed.Nodes.Count);
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

    [Fact]
    public void RejectedReportDoesNotMutateEarlierDestinationsOrActions()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("atomic", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("atomic", CreateReport(actions:
            [CreateAction("open-profile", "map", "open-profile", "queued")]), 1).IsSuccess);
        var before = System.Text.Json.JsonSerializer.Serialize(store.Get("atomic"));
        var rejected = CreateReport(destinations: [CreateDestination("extra", "Extra", "complete")], actions:
        [
            CreateAction("open-profile", "map", "open-profile", "blocked"),
            CreateAction("open-profile", "missing-destination", "different-control", "queued")
        ]);
        Assert.False(store.ApplyReport("atomic", rejected, 2).IsSuccess);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(store.Get("atomic")));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("host")]
    [InlineData("action")]
    [InlineData("edge")]
    public void ReportRejectsUnknownReferencesWithoutPartialWrites(string scenario)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("refs", "session", "app", "Default", DateTimeOffset.UtcNow);
        var report = CreateReport();
        if (scenario == "current") report["currentDestinationId"] = "missing";
        if (scenario == "host") report["navigationHosts"]!.AsArray().Add(
            CreateNestedHost("map", "flyout", "flyout_page", "missing"));
        if (scenario == "action") report["actions"]!.AsArray().Add(CreateAction("bad", "missing", "button", "queued"));
        if (scenario == "edge") report["transitions"]!.AsArray().Add(CreateTransition("bad", "missing"));
        var result = store.ApplyReport("refs", report, 1);
        Assert.False(result.IsSuccess);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Get("refs")!.Nodes);
    }

    [Fact]
    public void ResumedPromptIncludesCanonicalInventoryAndScrollState()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("inventory", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("inventory", CreateReport(
            actions: [CreateAction("canonical-action", "map", "open-profile", "queued")],
            transitions: [new JsonObject
            {
                ["id"] = "canonical-edge", ["from"] = "map", ["to"] = "profile",
                ["automationId"] = "open-profile", ["semanticMeaning"] = "View profile"
            }]), 1).IsSuccess);
        var prompt = SimulatorAgentService.BuildAppGraphFrontierText(store.Get("inventory"));
        Assert.Contains("canonical-edge", prompt);
        Assert.Contains("\"scrollStatus\":\"in_progress\"", prompt);
        Assert.Contains("\"id\":\"profile\"", prompt);
        Assert.Contains("Retained canonical graph inventory", prompt);
    }

    [Fact]
    public void RetiredAliasCanBeReconciledWithoutLosingStableIdentityOrBlockingCanonicalAction()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("aliases", "session", "app", "Default", DateTimeOffset.UtcNow);
        var canonical = CreateAction("canonical", "map", "home-tab-profile", "navigation");
        Assert.True(store.ApplyReport("aliases", CreateReport(actions: [canonical]), 1).IsSuccess);
        var alias = CreateAction("retired", "map", "", "unavailable");
        alias["selector"] = new JsonObject { ["automationId"] = "home-tab-profile" };
        alias["lastOutcome"] = "Obsolete duplicate of canonical; retained only as an observation.";
        Assert.True(store.ApplyReport("aliases", CreateReport(actions: [alias]), 2).IsSuccess);
        var retired = store.Get("aliases")!.Frontier.Single(action => action.Id == "retired");
        Assert.True(retired.HasStableAutomationId);
        Assert.Equal("home-tab-profile", retired.AutomationId);
        Assert.False(store.TryBuildActionGuardMessage("aliases", "map", "ansight_tap_ui", "home-tab-profile", 2, out _));
        store.RecordActionAttempt("aliases", "map", "ansight_tap_ui", alias["selector"]!.AsObject(), true, "Tab selected.");
        Assert.Equal(1, store.Get("aliases")!.Frontier.Single(action => action.Id == "canonical").AttemptCount);
        Assert.Equal(0, store.Get("aliases")!.Frontier.Single(action => action.Id == "retired").AttemptCount);
    }

    [Fact]
    public void NavigationRevalidationCanRestoreCanonicalAfterEarlierRetiredAlias()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("retired-aliases", "session", "app", "Default", DateTimeOffset.UtcNow);
        var alias = CreateAction("alias", "map", "home-tab-profile", "unavailable");
        alias["lastOutcome"] = "Obsolete alias.";
        var canonical = CreateAction("canonical", "map", "home-tab-profile", "unavailable");
        canonical["lastOutcome"] = "Incorrectly retired duplicate; needs revalidation.";
        Assert.True(store.ApplyReport("retired-aliases", CreateReport(actions: [alias, canonical]), 1).IsSuccess);
        canonical = canonical.DeepClone().AsObject();
        canonical["status"] = "navigation";
        canonical["lastOutcome"] = "Fresh observation confirms the existing Profile tab is visible and enabled.";
        Assert.True(store.ApplyReport("retired-aliases", CreateReport(actions: [canonical]), 2).IsSuccess);
        Assert.False(store.TryBuildActionGuardMessage("retired-aliases", "map", "ansight_tap_ui", "home-tab-profile", 2, out _));
    }

    [Theory]
    [InlineData("unavailable", true)]
    [InlineData("unsafe", false)]
    public void ExplicitNavigationRevalidationDoesNotOverrideUnsafeExclusion(string previousStatus, bool allowed)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("revalidate", "session", "app", "Default", DateTimeOffset.UtcNow);
        var action = CreateAction("tab", "map", "home-tab-profile", previousStatus);
        Assert.True(store.ApplyReport("revalidate", CreateReport(actions: [action]), 1).IsSuccess);
        var revalidated = action.DeepClone().AsObject();
        revalidated["status"] = "navigation";
        revalidated["lastOutcome"] = "Fresh observation confirms the existing Profile tab is visible and enabled.";
        Assert.Equal(allowed, store.ApplyReport("revalidate", CreateReport(actions: [revalidated]), 2).IsSuccess);
    }

    [Fact]
    public void TabGroupUpdatesSelectionAndRejectsDuplicateIdentity()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("tabs", "session", "app", "Default", DateTimeOffset.UtcNow);
        var group = new JsonObject
        {
            ["id"] = "sections", ["parentDestinationId"] = "profile",
            ["selectedDestinationId"] = "one", ["tabDestinationIds"] = new JsonArray("one", "two")
        };
        var report = CreateReport(destinations:
        [
            CreateDestination("profile", "Profile", "not_scrollable"),
            CreateDestination("one", "One", "not_scrollable", "state", "profile"),
            CreateDestination("two", "Two", "not_scrollable", "state", "profile")
        ], tabGroups: [group]);
        Assert.True(store.ApplyReport("tabs", report, 1).IsSuccess);
        group["selectedDestinationId"] = "two";
        Assert.True(store.ApplyReport("tabs", report, 2).IsSuccess);
        Assert.Equal("two", Assert.Single(store.Get("tabs")!.TabGroups).SelectedDestinationId);
        group["id"] = "alias";
        Assert.False(store.ApplyReport("tabs", report, 3).IsSuccess);
        Assert.Single(store.Get("tabs")!.TabGroups);
    }

    [Fact]
    public void RediscoveredActionsReuseCanonicalIdsAndPreserveCompletedAttempts()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("aliases", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("aliases", CreateReport(actions: [CreateAction("original", "map", "open-profile", "queued")]), 1).IsSuccess);
        store.RecordActionAttempt("aliases", "map", "ansight_tap_ui", new JsonObject { ["automationId"] = "open-profile" }, true, "Opened.");
        Assert.True(store.ApplyReport("aliases", CreateReport(actions: [CreateAction("original", "map", "open-profile", "explored")]), 2).IsSuccess);
        var update = store.ApplyReport("aliases", CreateReport(actions: [CreateAction("alias", "map", "open-profile", "attempted")]), 3);
        Assert.True(update.IsSuccess, update.Message);
        Assert.Equal("original", update.ReconciledActionIds["alias"]);
        var action = Assert.Single(store.Get("aliases")!.Frontier);
        Assert.Equal("explored", action.Status);
        Assert.Equal(1, action.AttemptCount);
    }

    [Fact]
    public void ReturnActionsRetainBothCallersAndReconcileRepeatedEdges()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("returns", "session", "app", "Default", DateTimeOffset.UtcNow);
        var first = CreateTransition("return", "profile");
        first["automationId"] = "guide-back-button";
        Assert.True(store.ApplyReport("returns", CreateReport(transitions: [first]), 1).IsSuccess);
        var second = first.DeepClone().AsObject();
        second["to"] = "map";
        var update = store.ApplyReport("returns", CreateReport(transitions: [second]), 2);
        Assert.True(update.IsSuccess, update.Message);
        Assert.NotEqual("return", update.ReconciledTransitionIds["return"]);
        Assert.Equal(2, store.Get("returns")!.Edges.Count);
        Assert.All(store.Get("returns")!.Edges, edge => Assert.Equal(edge.To, edge.NavigationContext!.ReturnDestinationId));
        var alias = first.DeepClone().AsObject();
        alias["id"] = "return-alias";
        update = store.ApplyReport("returns", CreateReport(transitions: [alias]), 3);
        Assert.True(update.IsSuccess, update.Message);
        Assert.Equal("return", update.ReconciledTransitionIds["return-alias"]);
        Assert.Equal(2, store.Get("returns")!.Edges.Count);
    }

    [Theory]
    [InlineData("profile", "open-profile", "ansight_tap_ui")]
    [InlineData("map", "different-control", "ansight_tap_ui")]
    [InlineData("map", "open-profile", "ansight_scroll_ui")]
    public void ReusedActionIdsRetainDifferentSourcesTargetsAndTools(
        string destinationId, string automationId, string toolName)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("collision", "session", "app", "Default", DateTimeOffset.UtcNow);
        var original = CreateAction("reused", "map", "open-profile", "unsafe");
        Assert.True(store.ApplyReport("collision", CreateReport(actions: [original]), 1).IsSuccess);
        var originalSnapshot = store.Get("collision")!.Frontier.Single();

        var observed = CreateAction("reused", destinationId, automationId, "queued");
        observed["toolName"] = toolName;
        var update = store.ApplyReport("collision", CreateReport(actions: [observed]), 2);
        Assert.True(update.IsSuccess, update.Message);
        var canonicalId = update.ReconciledActionIds["reused"];
        Assert.NotEqual("reused", canonicalId);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(originalSnapshot),
            System.Text.Json.JsonSerializer.Serialize(store.Get("collision")!.Frontier.Single(action => action.Id == "reused")));
        Assert.Equal("queued", store.Get("collision")!.Frontier.Single(action => action.Id == canonicalId).Status);

        update = store.ApplyReport("collision", CreateReport(actions: [observed.DeepClone().AsObject()]), 3);
        Assert.True(update.IsSuccess, update.Message);
        Assert.Equal(canonicalId, update.ReconciledActionIds["reused"]);
        Assert.Equal(2, store.Get("collision")!.Frontier.Count);
    }

    [Fact]
    public void CollisionWithAnExistingCanonicalActionPreservesItsAttemptsAndStatus()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("canonical", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("canonical", CreateReport(actions:
        [
            CreateAction("reused", "map", "open-profile", "unsafe"),
            CreateAction("canonical", "profile", "details", "queued")
        ]), 1).IsSuccess);
        store.RecordActionAttempt("canonical", "profile", "ansight_tap_ui",
            new JsonObject { ["automationId"] = "details" }, true, "Opened details.");
        Assert.True(store.ApplyReport("canonical", CreateReport(actions:
            [CreateAction("canonical", "profile", "details", "explored")]), 2).IsSuccess);

        var update = store.ApplyReport("canonical", CreateReport(actions:
            [CreateAction("reused", "profile", "details", "queued")]), 3);
        Assert.True(update.IsSuccess, update.Message);
        Assert.Equal("canonical", update.ReconciledActionIds["reused"]);
        var actions = store.Get("canonical")!.Frontier;
        Assert.Equal(2, actions.Count);
        Assert.Equal("explored", actions.Single(action => action.Id == "canonical").Status);
        Assert.Equal(1, actions.Single(action => action.Id == "canonical").AttemptCount);
        Assert.Equal("unsafe", actions.Single(action => action.Id == "reused").Status);
    }

    [Theory]
    [InlineData("profile", "filter-toggle")]
    [InlineData("map", "another-filter-toggle")]
    public void ReusedTransitionIdsPreserveEachObservedSourceAndAction(string from, string automationId)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("edges", "session", "app", "Default", DateTimeOffset.UtcNow);
        var original = CreateTransition("reused", "profile");
        Assert.True(store.ApplyReport("edges", CreateReport(transitions: [original]), 1).IsSuccess);
        var originalSnapshot = store.Get("edges")!.Edges.Single();

        var observed = CreateTransition("reused", "map");
        observed["from"] = from;
        observed["automationId"] = automationId;
        var update = store.ApplyReport("edges", CreateReport(transitions: [observed]), 2);
        Assert.True(update.IsSuccess, update.Message);
        var canonicalId = update.ReconciledTransitionIds["reused"];
        Assert.NotEqual("reused", canonicalId);
        Assert.Equal(originalSnapshot, store.Get("edges")!.Edges.Single(edge => edge.Id == "reused"));

        update = store.ApplyReport("edges", CreateReport(transitions: [observed.DeepClone().AsObject()]), 3);
        Assert.True(update.IsSuccess, update.Message);
        Assert.Equal(canonicalId, update.ReconciledTransitionIds["reused"]);
        Assert.Equal(2, store.Get("edges")!.Edges.Count);
    }

    [Fact]
    public void ReusedTransitionIdDoesNotInheritUnrelatedReturnSemantics()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("semantics", "session", "app", "Default", DateTimeOffset.UtcNow);
        var back = CreateTransition("reused", "profile");
        back["automationId"] = "back-button";
        Assert.True(store.ApplyReport("semantics", CreateReport(transitions: [back]), 1).IsSuccess);

        var observed = CreateTransition("reused", "profile");
        var update = store.ApplyReport("semantics", CreateReport(transitions: [observed]), 2);
        Assert.True(update.IsSuccess, update.Message);
        var edge = store.Get("semantics")!.Edges.Single(candidate => candidate.Id == update.ReconciledTransitionIds["reused"]);
        Assert.Equal(string.Empty, edge.NavigationKind);
        Assert.Null(edge.NavigationContext);

        observed = observed.DeepClone().AsObject();
        observed["to"] = "map";
        update = store.ApplyReport("semantics", CreateReport(transitions: [observed]), 3);
        Assert.False(update.IsSuccess);
        Assert.Contains("already leads to", update.Message);
        Assert.Equal(2, store.Get("semantics")!.Edges.Count);
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
            ["currentDestinationId"] = destinations?.FirstOrDefault()?["id"]?.GetValue<string>() ?? "map",
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
