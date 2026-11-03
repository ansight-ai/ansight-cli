using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class SimulatorAgentAppGraphLiveRunStoreTests
{
    [Fact]
    public void EnteringKnownScreenCanRestoreContainedStateWithoutChangingItsEntryRoute()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(transitions:
            [CreateTransition("open-profile", "profile")]), 1).IsSuccess);
        var selector = new JsonObject { ["automationId"] = "map-open-filters-button" };
        var pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
            selector, "open", "evidence", "Delivered.")!;
        var report = CreateReport(destinations:
            [CreateDestination("profile-tab", "Profile tab", "not_scrollable", "state", "profile")]);
        report["currentDestinationId"] = "profile-tab";
        report["actionOutcome"] = Outcome(pending, "verified");
        Assert.True(store.ApplyReport("run", report, 2).IsSuccess);

        var run = store.Get("run")!;
        Assert.Null(run.PendingOutcome);
        Assert.Equal("profile-tab", run.CurrentDestinationId);
        Assert.Equal("profile", Assert.Single(run.Edges).To);
        Assert.Equal("profile-tab", Assert.Single(run.Frontier).ResultDestinationId);
        Assert.False(store.TryBuildActionGuardMessage("run", "map", "ansight_tap_ui",
            "map-open-filters-button", 2, out _, selector));

        var observed = CreateTransition("observed-child", "profile-tab");
        Assert.True(store.ApplyReport("run", CreateReport(transitions: [observed]), 3).IsSuccess);
        Assert.Single(store.Get("run")!.Edges);
        var unrelated = CreateTransition("unrelated", "map");
        Assert.False(store.ApplyReport("run", CreateReport(transitions: [unrelated]), 4).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransitionAttributionCorrectionsAreAtomicAndMergeExistingCanonicalEdges(bool canonicalExists)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        var wrong = CreateTransition("misattributed-close", "profile");
        wrong["navigationKind"] = "dismiss";
        Assert.True(store.ApplyReport("run", CreateReport(transitions: [wrong]), 1).IsSuccess);
        var canonical = CreateTransition("canonical-close", "profile");
        canonical["from"] = "dialog";
        canonical["navigationKind"] = "dismiss";
        Assert.True(store.ApplyReport("run", CreateReport(
            destinations: [CreateDestination("dialog", "Details", "not_scrollable")],
            transitions: canonicalExists ? [canonical] : []), 2).IsSuccess);

        var correction = new JsonObject
        {
            ["id"] = "misattributed-close", ["from"] = "missing", ["to"] = "profile",
            ["evidence"] = "The retained before-state shows Details; closing returned to Profile."
        };
        var report = CreateReport();
        report["transitionCorrections"] = new JsonArray(correction);
        Assert.False(store.ApplyReport("run", report, 3).IsSuccess);
        Assert.Equal("map", store.Get("run")!.Edges.Single(edge => edge.Id == "misattributed-close").From);

        correction["from"] = "dialog";
        Assert.True(store.ApplyReport("run", report, 4).IsSuccess);
        var edge = Assert.Single(store.Get("run")!.Edges);
        Assert.Equal(canonicalExists ? "canonical-close" : "misattributed-close", edge.Id);
        Assert.Equal("dialog", edge.From);
        Assert.Equal("profile", edge.To);
        Assert.Equal("profile", edge.NavigationContext!.ReturnDestinationId);
    }

    [Fact]
    public void UnverifiedOutcomeMayKeepDestinationUnknownButCannotUnlockInput()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
            new JsonObject { ["automationId"] = "open" }, "input", "evidence", "Delivered.")!;
        var report = CreateReport();
        report["currentDestinationId"] = "";
        report["actionOutcome"] = Outcome(pending, "needs_verification");

        Assert.True(store.ApplyReport("run", report, 2).IsSuccess);
        Assert.Null(store.Get("run")!.CurrentDestinationId);
        Assert.Equal(pending.InputId, store.Get("run")!.PendingOutcome!.InputId);

        report["actionOutcome"] = Outcome(pending, "verified");
        Assert.False(store.ApplyReport("run", report, 3).IsSuccess);
        report["currentDestinationId"] = "profile";
        Assert.True(store.ApplyReport("run", report, 4).IsSuccess);
        Assert.Null(store.Get("run")!.PendingOutcome);
    }

    [Theory]
    [InlineData("verified", false)]
    [InlineData("no_change", true)]
    public void SameScreenStateChangeAllowsABoundedReturnInteractionButNoOpDoesNot(string outcome, bool guarded)
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(actions:
            [CreateAction("untouched", "map", "another-control", "queued")]), 1).IsSuccess);
        var selector = new JsonObject { ["automationId"] = "sort" };
        var pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
            selector, "input", "evidence", "Delivered.")!;
        var report = CreateReport();
        report["actionOutcome"] = Outcome(pending, outcome);
        Assert.True(store.ApplyReport("run", report, 2).IsSuccess);

        Assert.Equal(guarded, store.TryBuildActionGuardMessage("run", "map", "ansight_tap_ui", "sort", 2, out _));
        if (!guarded)
        {
            pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
                selector, "restore", "restore-evidence", "Delivered.")!;
            report = CreateReport();
            report["actionOutcome"] = Outcome(pending, "verified");
            Assert.True(store.ApplyReport("run", report, 3).IsSuccess);
            Assert.True(store.TryBuildActionGuardMessage("run", "map", "ansight_tap_ui", "sort", 2, out _));
        }
    }

    [Fact]
    public void ObservedCloseOutcomeRemainsReusableWithoutPriorNavigationClassification()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
            new JsonObject { ["automationId"] = "form-close-button" }, "input", "evidence", "Delivered.")!;
        var report = CreateReport();
        report["currentDestinationId"] = "profile";
        report["actionOutcome"] = Outcome(pending, "verified");
        report["actionOutcome"]!["navigationKind"] = "dismiss";

        Assert.True(store.ApplyReport("run", report, 2).IsSuccess);

        var run = store.Get("run")!;
        Assert.Equal("navigation", Assert.Single(run.Frontier).Status);
        Assert.Equal("dismiss", Assert.Single(run.Edges).NavigationKind);
        Assert.Equal("profile", Assert.Single(run.Edges).NavigationContext!.ReturnDestinationId);
        Assert.False(store.TryBuildActionGuardMessage("run", "map", "ansight_tap_ui", "form-close-button", 1, out _));
    }

    [Fact]
    public void SelectorFreeInputStillCreatesAnExactPendingObligation()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var pending = store.RecordDeliveredInput("run", "map", "ansight_scroll_ui",
            new JsonObject(), "input", "evidence", "Viewport scroll delivered.");
        Assert.Equal(Assert.Single(store.Get("run")!.Frontier).Id, pending!.ActionId);
        Assert.Equal("needs_verification", Assert.Single(store.Get("run")!.Frontier).Status);
    }

    [Fact]
    public void ScreenOnlyAndWrongInputReportsCannotDischargeAnOutcome()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var pending = store.RecordDeliveredInput("run", "map", "ansight_tap_ui",
            new JsonObject { ["automationId"] = "open-profile" }, "input-1", "evidence-1", "Tap delivered.")!;

        var report = CreateReport();
        report["currentDestinationId"] = "profile";
        Assert.False(store.ApplyReport("run", report, 2).IsSuccess);
        Assert.Null(store.Get("run")!.CurrentDestinationId);
        report["actionOutcome"] = Outcome(pending, "verified");
        report["actionOutcome"]!["inputId"] = "another-input";
        Assert.False(store.ApplyReport("run", report, 3).IsSuccess);
        report["actionOutcome"]!["inputId"] = pending.InputId;
        report["actionOutcome"]!["evidenceRefs"] = new JsonArray("unrelated-evidence");
        Assert.False(store.ApplyReport("run", report, 4).IsSuccess);

        report["actionOutcome"] = Outcome(pending, "verified");
        Assert.True(store.ApplyReport("run", report, 5).IsSuccess);
        var run = store.Get("run")!;
        Assert.Null(run.PendingOutcome);
        Assert.Equal("explored", Assert.Single(run.Frontier).Status);
        Assert.Equal("profile", Assert.Single(run.Edges).To);
        Assert.Equal(1, Assert.Single(run.Frontier).AttemptCount);
    }

    [Fact]
    public void DeferredAndBlockedWorkNeverBecomeCompletedCoverage()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var pending = store.RecordDeliveredInput("run", "map", "ansight_scroll_ui",
            new JsonObject { ["automationId"] = "list" }, "input", "evidence", "Scroll delivered.")!;
        var report = CreateReport(destinations: [CreateDestination("map", "Map", "blocked")]);
        report["actionOutcome"] = Outcome(pending, "blocked");
        report["actionOutcome"]!["blockerKind"] = "budget";
        Assert.False(store.ApplyReport("run", report, 2).IsSuccess);

        report["actionOutcome"] = Outcome(pending, "deferred");
        Assert.True(store.ApplyReport("run", report, 3).IsSuccess);
        var run = store.Get("run")!;
        Assert.Null(run.PendingOutcome);
        Assert.Equal("deferred", Assert.Single(run.Frontier).Status);
        Assert.Equal("in_progress", run.Nodes.Single(node => node.Id == "map").ScrollStatus);
        Assert.Equal(0, run.Coverage.ActionsExplored);
        Assert.Equal(1, run.Coverage.ScrollContainersCompleted); // Only the non-scrolling Profile.
        Assert.Single(AppGraphFrontier.Pending(run.Frontier, run.Nodes, "map"));

        pending = store.RecordDeliveredInput("run", "map", "ansight_scroll_ui",
            new JsonObject { ["automationId"] = "list" }, "input-2", "evidence-2", "Scroll delivered.")!;
        report = CreateReport(destinations: [CreateDestination("map", "Map", "blocked")]);
        report["actionOutcome"] = Outcome(pending, "blocked");
        Assert.True(store.ApplyReport("run", report, 4).IsSuccess);
        Assert.Equal(0, store.Get("run")!.Coverage.ActionsExplored);
        Assert.Equal(1, store.Get("run")!.Coverage.ScrollContainersCompleted);
    }

    [Fact]
    public void PendingOutcomeSurvivesDatabaseResumeAndStillRequiresEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ansight-outcome-{Guid.NewGuid():N}");
        try
        {
            var database = new AppGraphExplorationDatabase(new DataToolApplicationPaths(root));
            var store = new AppGraphLiveRunStore(database);
            store.Begin("first", "session", "app", "Default", DateTimeOffset.UtcNow);
            Assert.True(store.ApplyReport("first", CreateReport(), 1).IsSuccess);
            var pending = store.RecordDeliveredInput("first", "map", "ansight_tap_ui",
                new JsonObject { ["automationId"] = "open" }, "input", "retained-evidence", "Delivered.");
            var resumed = new AppGraphLiveRunStore(database);
            resumed.Begin("next", "new-session", "app", "Default", DateTimeOffset.UtcNow);
            var restored = resumed.Get("next")!.PendingOutcome!;
            Assert.Equal(pending!.InputId, restored.InputId);
            Assert.Equal(pending.ActionId, restored.ActionId);
            Assert.Equal(pending.EvidenceId, restored.EvidenceId);
            Assert.Equal("open", restored.Selector["automationId"]!.GetValue<string>());
            Assert.Equal("open", restored.SemanticMeaning);
            Assert.False(resumed.ApplyReport("next", CreateReport(), 1).IsSuccess);
            var report = CreateReport();
            report["actionOutcome"] = Outcome(pending!, "deferred");
            Assert.True(resumed.ApplyReport("next", report, 2).IsSuccess);
            Assert.Single(resumed.Get("next")!.Frontier);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void SameNamedStatesNeedDistinguishingEvidenceAndDuplicatesCanBeExplicitlyMerged()
    {
        var store = new AppGraphLiveRunStore();
        store.Begin("run", "session", "app", "Default", DateTimeOffset.UtcNow);
        Assert.True(store.ApplyReport("run", CreateReport(), 1).IsSuccess);
        var duplicate = CreateDestination("map-copy", "Map", "unknown");
        Assert.False(store.ApplyReport("run", CreateReport(destinations: [duplicate]), 2).IsSuccess);
        duplicate["identityEvidence"] = "A different map inside a route detail context.";
        Assert.True(store.ApplyReport("run", CreateReport(destinations: [duplicate.DeepClone().AsObject()]), 3).IsSuccess);
        var edge = CreateTransition("open-copy", "map-copy");
        Assert.True(store.ApplyReport("run", CreateReport(transitions: [edge]), 4).IsSuccess);
        var merge = CreateReport();
        merge["destinationAliases"] = new JsonArray(new JsonObject
        {
            ["from"] = "map-copy", ["to"] = "map",
            ["evidence"] = "Reinspection confirms the same route and visual state; the prior context distinction was incorrect."
        });
        Assert.True(store.ApplyReport("run", merge, 5).IsSuccess);
        Assert.DoesNotContain(store.Get("run")!.Nodes, node => node.Id == "map-copy");
        Assert.Equal("map", Assert.Single(store.Get("run")!.Edges).To);
    }

    private static JsonObject Outcome(SimulatorAgentAppGraphPendingOutcome pending, string status) => new()
    {
        ["inputId"] = pending.InputId, ["actionId"] = pending.ActionId, ["status"] = status,
        ["evidenceRefs"] = new JsonArray(pending.EvidenceId),
        ["evidence"] = "The retained after-state shows the current page and its visible content.",
        ["reason"] = status == "verified" ? "" : "More content requires inspection.",
        ["blockerKind"] = status == "blocked" ? "observation" : ""
    };
}
