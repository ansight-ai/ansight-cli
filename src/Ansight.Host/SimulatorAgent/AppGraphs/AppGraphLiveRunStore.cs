using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed partial class AppGraphLiveRunStore
{
    private const int MaximumRetainedRuns = 12;
    private const int MaximumRetainedTraceEntries = 12_288;
    private static readonly HashSet<string> supportedKinds =
        new(["screen", "dialog", "state"], StringComparer.Ordinal);
    private static readonly HashSet<string> supportedScrollStatuses =
        new(["unknown", "not_scrollable", "in_progress", "complete", "blocked"], StringComparer.Ordinal);
    private static readonly HashSet<string> supportedActionStatuses =
        new(["queued", "attempted", "needs_verification", "deferred", "explored", "blocked", "unsafe", "unavailable", "navigation"], StringComparer.Ordinal);
    private static readonly HashSet<string> supportedNavigationHostKinds =
        new(["flyout", "drawer", "bottom_tabs", "top_tabs", "navigation_rail", "shell", "other"], StringComparer.Ordinal);
    private static readonly HashSet<string> terminalActionStatuses =
        new(["explored", "blocked", "unsafe", "unavailable"], StringComparer.Ordinal);

    private readonly Lock gate = new();
    private readonly Dictionary<string, MutableRun> runs = new(StringComparer.Ordinal);
    private readonly IAppGraphExplorationDatabase database;

    public AppGraphLiveRunStore(
        IAppGraphExplorationDatabase? database = null)
    {
        this.database = database ?? NullAppGraphExplorationDatabase.Instance;
    }

    public void Begin(
        string runId,
        string sessionId,
        string? appId,
        string graphName,
        DateTimeOffset startedUtc)
    {
        lock (gate)
        {
            var resumeState = database.LoadLatestIncomplete(sessionId, appId, graphName);
            var run = new MutableRun(
                runId,
                sessionId,
                appId,
                graphName,
                startedUtc);

            if (resumeState is not null)
            {
                run.PendingOutcome = resumeState.PendingOutcome;

                foreach (var node in resumeState.Nodes)
                {
                    run.Nodes[node.Id] = node;
                }

                foreach (var edge in resumeState.Edges)
                {
                    run.Edges[edge.Id] = edge;
                }

                foreach (var navigationHost in resumeState.NavigationHosts)
                {
                    run.NavigationHosts[navigationHost.Id] = navigationHost;
                }

                foreach (var tabGroup in resumeState.TabGroups.OrderBy(group => group.FirstObservedUtc))
                {
                    if (run.TabGroups.Values.Any(group => group.ParentDestinationId == tabGroup.ParentDestinationId
                        && group.TabDestinationIds.SequenceEqual(tabGroup.TabDestinationIds)))
                    {
                        continue;
                    }

                    run.TabGroups[tabGroup.Id] = tabGroup;
                }

                foreach (var action in resumeState.Actions)
                {
                    var automationId = NormalizeOptional(action.AutomationId)
                        ?? NormalizeOptional(ReadString(action.Selector, "automationId")) ?? string.Empty;
                    var navigationKind = AppGraphActionIdentity.NavigationKind(action.ToolName, automationId, action.Selector, action.NavigationKind);
                    const string legacyRecheckPrefix = "Rechecking a prior-run blocker: ";
                    var wasAutomaticallyRequeued = action.Status == "queued"
                        && action.AttemptCount == 0
                        && action.LastOutcome.StartsWith(legacyRecheckPrefix, StringComparison.Ordinal);
                    var retainedStatus = wasAutomaticallyRequeued ? "blocked" : action.Status;
                    run.Actions[action.Id] = action with
                    {
                        AutomationId = automationId,
                        Selector = AppGraphActionIdentity.NormalizeSelector(action.Selector),
                        NavigationKind = navigationKind,
                        Status = navigationKind.Length > 0
                            && retainedStatus is not ("unsafe" or "blocked" or "unavailable") ? "navigation"
                            : retainedStatus,
                        LastOutcome = wasAutomaticallyRequeued
                            ? action.LastOutcome[legacyRecheckPrefix.Length..] : action.LastOutcome
                    };
                }

                foreach (var edge in run.Edges.Values.ToArray())
                {
                    var action = FindMatchingAction(run.Actions.Values, edge.From,
                        "ansight_tap_ui", edge.AutomationId, edge.Selector);
                    var navigationKind = AppGraphActionIdentity.NavigationKind(
                        "ansight_tap_ui", edge.AutomationId, edge.Selector,
                        NormalizeOptional(edge.NavigationKind) ?? action?.NavigationKind);
                    run.Edges[edge.Id] = edge with
                    {
                        NavigationKind = navigationKind,
                        NavigationContext = navigationKind is "back" or "pop" or "dismiss"
                            ? new SimulatorAgentAppGraphNavigationContext(edge.To)
                            : edge.NavigationContext
                    };
                }

                if (run.PendingOutcome is { } pending && run.Actions.TryGetValue(pending.ActionId, out var pendingAction))
                {
                    run.PendingOutcome = pending with
                    {
                        SemanticMeaning = pendingAction.SemanticMeaning,
                        Selector = pendingAction.Selector.DeepClone().AsObject()
                    };
                }

                run.Coverage = DeriveCoverage(run, SimulatorAgentAppGraphLiveCoverage.Empty);
                run.Message = $"Resumed the incomplete frontier from run '{resumeState.RunId}'.";
            }

            runs[runId] = run;
            TrimRetainedRuns();
            database.Save(CreateSnapshot(run));
        }
    }

    public IReadOnlyList<SimulatorAgentAppGraphLiveRun> List(string? sessionId = null)
    {
        lock (gate)
        {
            var liveSnapshots = runs.Values
                .Where(run => string.IsNullOrWhiteSpace(sessionId)
                    || string.Equals(run.SessionId, sessionId.Trim(), StringComparison.Ordinal))
                .Select(CreateSnapshot)
                .ToArray();

            var retainedSnapshots = database.List(sessionId, MaximumRetainedRuns)
                .Select(static run => run.Status is "starting" or "running"
                    ? run with
                    {
                        Status = "partial",
                        ActiveToolName = null,
                        Message = "Exploration was interrupted. Retained coverage is incomplete and can be resumed."
                    } : run);

            return liveSnapshots.Concat(retainedSnapshots)
                .DistinctBy(static run => run.RunId, StringComparer.Ordinal)
                .OrderByDescending(static run => run.UpdatedUtc)
                .Take(MaximumRetainedRuns)
                .ToArray();
        }
    }

    public SimulatorAgentAppGraphLiveRun? Get(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (gate)
        {
            return runs.TryGetValue(runId.Trim(), out var run)
                ? CreateSnapshot(run)
                : null;
        }
    }

    public IReadOnlyList<string> GetStructureGaps(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        lock (gate)
        {
            return runs.TryGetValue(runId.Trim(), out var run)
                ? BuildStructureGaps(run)
                : ["The live App Graph run is no longer available."];
        }
    }

    public void InvalidateCurrentDestination(string runId)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.CompletedUtc.HasValue)
            {
                return;
            }

            run.CurrentDestinationId = null;
            run.UpdatedUtc = DateTimeOffset.UtcNow;
            database.Save(CreateSnapshot(run));
        }
    }

    public void UpdateActivity(
        string runId,
        string message,
        int turn,
        string? activeToolName = null)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.CompletedUtc.HasValue)
            {
                return;
            }

            run.Status = "running";
            run.Message = NormalizeOptional(message) ?? run.Message;
            run.Turn = Math.Max(run.Turn, turn);
            run.ActiveToolName = NormalizeOptional(activeToolName);
            run.UpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public void AppendTrace(
        string runId,
        SimulatorAgentProgressStage stage,
        string message,
        int turn,
        string? toolName = null)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                return;
            }

            var occurredUtc = DateTimeOffset.UtcNow;
            run.Trace.Add(new SimulatorAgentAppGraphLiveTraceEntry(
                ++run.TraceSequence,
                stage.ToString(),
                NormalizeOptional(message) ?? stage.ToString(),
                Math.Max(0, turn),
                NormalizeOptional(toolName),
                occurredUtc));

            if (run.Trace.Count > MaximumRetainedTraceEntries)
            {
                run.Trace.RemoveRange(0, run.Trace.Count - MaximumRetainedTraceEntries);
            }

            run.UpdatedUtc = occurredUtc;
        }
    }

    public void RebindSession(string runId, string sessionId)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                return;
            }

            run.SessionId = sessionId;
            run.UpdatedUtc = DateTimeOffset.UtcNow;
        }
    }

    public AppGraphLiveUpdateResult ApplyReport(
        string runId,
        JsonObject arguments,
        int turn)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                return AppGraphLiveUpdateResult.Failure(
                    "The live App Graph run is no longer available.");
            }

            if (arguments["destinations"] is not JsonArray destinationValues
                || arguments["transitions"] is not JsonArray transitionValues
                || arguments["actions"] is not JsonArray actionValues
                || arguments["coverage"] is not JsonObject coverageValue)
            {
                return AppGraphLiveUpdateResult.Failure(
                    "destinations, transitions, actions, and coverage are required.");
            }

            var navigationHostValues = arguments["navigationHosts"] as JsonArray ?? [];
            var tabGroupValues = arguments["tabGroups"] as JsonArray ?? [];

            // Validate a staged copy. A rejected report must not leave its earlier
            // destinations, ownership changes, or action statuses in the live graph.
            run = run.CopyForReport();

            var aliasError = ApplyDestinationAliases(run, arguments);

            if (aliasError is not null)
            {
                return AppGraphLiveUpdateResult.Failure(aliasError);
            }

            var now = DateTimeOffset.UtcNow;
            var destinationCount = 0;

            foreach (var value in destinationValues)
            {
                if (value is JsonObject candidate && ValidateNewDestinationIdentity(candidate, run) is { } identityError)
                {
                    return AppGraphLiveUpdateResult.Failure(identityError);
                }

                SimulatorAgentAppGraphLiveNode? node = null;
                string? error = null;

                if (value is not JsonObject destination
                    || !TryReadDestination(destination, run.Nodes, now, out node, out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "A destination update was invalid.");
                }

                run.Nodes[node!.Id] = node;
                destinationCount++;
            }

            var navigationHostCount = 0;

            foreach (var value in navigationHostValues)
            {
                SimulatorAgentAppGraphLiveNavigationHost? navigationHost = null;
                string? error = null;

                if (value is not JsonObject navigationHostValue
                    || !TryReadNavigationHost(
                        navigationHostValue,
                        run.NavigationHosts,
                        now,
                        out navigationHost,
                        out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "A navigation host update was invalid.");
                }

                run.NavigationHosts[navigationHost!.Id] = navigationHost;
                navigationHostCount++;
            }

            var tabGroupCount = 0;

            foreach (var value in tabGroupValues)
            {
                SimulatorAgentAppGraphLiveTabGroup? tabGroup = null;
                string? error = null;

                if (value is not JsonObject tabGroupValue
                    || !TryReadTabGroup(
                        tabGroupValue,
                        run.TabGroups,
                        now,
                        out tabGroup,
                        out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "A tab group update was invalid.");
                }

                run.TabGroups[tabGroup!.Id] = tabGroup;
                tabGroupCount++;
            }

            ApplyStructureParents(run, now);

            var actionCount = 0;
            var reconciledActionIds = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var value in actionValues)
            {
                SimulatorAgentAppGraphLiveAction? action = null;
                string? error = null;

                if (value is not JsonObject actionValue
                    || !TryReadAction(actionValue, run.Actions, now, out action, out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "An action candidate update was invalid.");
                }

                var reportedId = ReadString(actionValue, "id")!;

                if (!string.Equals(reportedId, action!.Id, StringComparison.Ordinal))
                {
                    reconciledActionIds[reportedId] = action.Id;
                }

                run.Actions[action.Id] = action;
                actionCount++;
            }

            var transitionCount = 0;
            var reconciledTransitionIds = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var value in transitionValues)
            {
                SimulatorAgentAppGraphLiveEdge? edge = null;
                string? error = null;

                if (value is not JsonObject transition
                    || !TryReadTransition(transition, run.Edges, run.Nodes, run.Actions.Values, now, out edge, out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "A transition update was invalid.");
                }

                var reportedId = ReadString(transition, "id")!;

                if (!string.Equals(reportedId, edge!.Id, StringComparison.Ordinal))
                {
                    reconciledTransitionIds[reportedId] = edge.Id;
                }

                run.Edges[edge.Id] = edge;
                MarkTransitionActionExplored(run, edge, now);
                transitionCount++;
            }

            var correctionError = ApplyTransitionCorrections(run, arguments, now);

            if (correctionError is not null)
            {
                return AppGraphLiveUpdateResult.Failure(correctionError);
            }

            run.CurrentDestinationId = NormalizeOptional(ReadString(arguments, "currentDestinationId"));

            var referenceError = ValidateReportReferences(run, arguments);

            if (referenceError is not null)
            {
                return AppGraphLiveUpdateResult.Failure(referenceError);
            }

            var outcomeError = ResolvePendingOutcome(run, arguments, now);

            if (outcomeError is not null)
            {
                return AppGraphLiveUpdateResult.Failure(outcomeError);
            }

            run.Message = NormalizeOptional(ReadString(arguments, "message"))
                ?? $"Observed {run.Nodes.Count} destinations and {run.Edges.Count} transitions.";
            run.Coverage = DeriveCoverage(
                run,
                MergeCoverage(run.Coverage, coverageValue));
            run.Status = "running";
            run.Turn = Math.Max(run.Turn, turn);
            run.ActiveToolName = null;
            run.UpdatedUtc = now;

            var snapshot = CreateSnapshot(run);
            database.Save(snapshot);
            runs[runId] = run;

            return AppGraphLiveUpdateResult.Success(
                destinationCount,
                transitionCount,
                actionCount,
                navigationHostCount,
                tabGroupCount,
                run.Nodes.Count,
                run.Edges.Count,
                AppGraphFrontier.Pending(run.Actions.Values, run.Nodes.Values, run.CurrentDestinationId)) with
            {
                ReconciledActionIds = reconciledActionIds,
                ReconciledTransitionIds = reconciledTransitionIds
            };
        }
    }

    public void RecordActionAttempt(
        string runId,
        string destinationId,
        string toolName,
        JsonObject selector,
        bool succeeded,
        string outcome,
        bool persist = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(selector);
        selector = AppGraphActionIdentity.NormalizeSelector(selector);

        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.CompletedUtc.HasValue)
            {
                return;
            }

            var automationId = NormalizeOptional(ReadString(selector, "automationId")) ?? string.Empty;
            var existing = FindMatchingAction(
                run.Actions.Values,
                destinationId,
                toolName,
                automationId,
                selector);
            var now = DateTimeOffset.UtcNow;
            var id = existing?.Id ?? BuildActionId(destinationId, toolName, selector);
            var navigationKind = AppGraphActionIdentity.NavigationKind(toolName!, automationId, selector, existing?.NavigationKind);
            var status = existing is not null && existing.Status is ("unsafe" or "blocked" or "unavailable")
                ? existing.Status
                : navigationKind.Length > 0 ? "navigation"
                    : existing is not null && terminalActionStatuses.Contains(existing.Status)
                        ? existing.Status
                        : succeeded ? "attempted" : existing?.Status ?? "queued";
            run.Actions[id] = new SimulatorAgentAppGraphLiveAction(
                id,
                destinationId,
                toolName,
                automationId,
                selector.DeepClone().AsObject(),
                existing?.SemanticMeaning ?? automationId,
                status,
                (existing?.AttemptCount ?? 0) + 1,
                NormalizeOptional(outcome) ?? (succeeded ? "Action delivered." : "Action failed."),
                existing?.ResultDestinationId,
                existing?.FirstObservedUtc ?? now,
                now,
                navigationKind)
            {
                RecheckAttemptBaseline = existing?.RecheckAttemptBaseline ?? 0,
                LastRecheckReason = existing?.LastRecheckReason ?? string.Empty
            };
            run.Coverage = DeriveCoverage(run, run.Coverage);
            run.UpdatedUtc = now;

            if (persist)
            {
                database.Save(CreateSnapshot(run));
            }
        }
    }

    public bool TryBuildActionGuardMessage(
        string runId,
        string destinationId,
        string toolName,
        string automationId,
        int maximumAttempts,
        out string message,
        JsonObject? selector = null)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                message = string.Empty;

                return false;
            }

            var action = FindMatchingAction(
                run.Actions.Values,
                destinationId,
                toolName,
                automationId,
                selector);

            if (action is null)
            {
                message = string.Empty;

                return false;
            }

            var guidance = BuildFrontierGuidance(run);

            // Completion of a return path must never remove the ability to traverse it again.
            // Explicit unsafe/blocked/unavailable classifications still apply.
            if (AppGraphActionIdentity.NavigationKind(toolName, automationId, selector ?? action.Selector, action.NavigationKind).Length > 0
                && action.Status is not ("unsafe" or "blocked" or "unavailable"))
            {
                message = string.Empty;

                return false;
            }

            // An explored edge is also a route to other work. Its recorded transition

            // permits traversal without resetting exploration status or retrying a no-op.
            var hasVerifiedRoute = action.Status == "explored"
                && toolName == "ansight_tap_ui"
                && action.ResultDestinationId is { } resultDestinationId
                && resultDestinationId != destinationId
                && run.Edges.Values.Any(edge =>
                {
                    var sameEndpoints = edge.From == destinationId
                        && IsDestinationWithin(run.Nodes, resultDestinationId, edge.To);
                    var sameAction = action.AutomationId.Length > 0
                        ? edge.AutomationId == action.AutomationId
                        : AppGraphActionIdentity.HasObservedSelector(edge.Selector)
                            && JsonNode.DeepEquals(action.Selector, edge.Selector);

                    return sameEndpoints && sameAction;
                });

            if (hasVerifiedRoute)
            {
                message = string.Empty;

                return false;
            }

            var recheckBaseline = Math.Clamp(action.RecheckAttemptBaseline, 0, action.AttemptCount);
            var attemptsSinceRecheck = action.AttemptCount - recheckBaseline;
            // Sorting and filtering can change visible state without changing the
            // canonical screen ID. Preserve one bounded interaction to restore it.
            // A no_change outcome is deliberately insufficient for this exception.
            var canRestoreVerifiedState = action.Status == "explored"
                && toolName is ("ansight_tap_ui" or "ansight_type_text")
                && action.ResultDestinationId == destinationId
                && action.LastOutcome.StartsWith("verified:", StringComparison.Ordinal)
                && attemptsSinceRecheck < maximumAttempts;

            if (canRestoreVerifiedState)
            {
                message = string.Empty;

                return false;
            }

            if (terminalActionStatuses.Contains(action.Status))
            {
                message = RenderPrompt(
                    "action-guard-terminal",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AUTOMATION_ID"] = action.AutomationId,
                        ["DESTINATION_ID"] = action.DestinationId,
                        ["STATUS"] = action.Status,
                        ["FRONTIER_GUIDANCE"] = guidance
                    });

                return true;
            }

            var untriedHere = AppGraphFrontier.Pending(run.Actions.Values, run.Nodes.Values, destinationId)
                .FirstOrDefault(candidate =>
                {
                    var untouchedHere = candidate.DestinationId == destinationId
                        && candidate.AttemptCount == 0
                        && candidate.Status == "queued";

                    return untouchedHere;
                });

            if (attemptsSinceRecheck > 0 && untriedHere is not null)
            {
                message = $"Resolve the retained outcome through observation before repeating '{action.Id}'. "
                    + $"Untouched action '{untriedHere.Id}' remains on this destination. Navigation essentials remain available.";

                return true;
            }

            if (attemptsSinceRecheck >= maximumAttempts)
            {
                message = RenderPrompt(
                    "action-guard-attempted",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AUTOMATION_ID"] = action.AutomationId,
                        ["DESTINATION_ID"] = action.DestinationId,
                        ["ATTEMPT_COUNT"] = attemptsSinceRecheck.ToString(),
                        ["FRONTIER_GUIDANCE"] = guidance
                    });

                return true;
            }

            message = string.Empty;

            return false;
        }
    }

    public void MergeCompletionSummary(string runId, string? summary, int turn)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(summary) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (root is null
            || !string.Equals(
                ReadString(root, "schema"),
                "ansight.app-graph-exploration/v1",
                StringComparison.Ordinal))
        {
            return;
        }

        var destinations = root["destinations"] as JsonArray ?? [];
        var transitions = root["transitions"] as JsonArray ?? [];
        var navigationHosts = root["navigationHosts"] as JsonArray ?? [];
        var tabGroups = root["tabGroups"] as JsonArray ?? [];
        var report = new JsonObject
        {
            ["message"] = ReadString(root, "summary") ?? "Exploration completed.",
            ["currentDestinationId"] = string.Empty,
            ["destinations"] = new JsonArray(destinations
                .OfType<JsonObject>()
                .Select(destination => new JsonObject
                {
                    ["id"] = ReadString(destination, "id") ?? string.Empty,
                    ["kind"] = ReadString(destination, "kind") ?? "screen",
                    ["name"] = ReadString(destination, "name") ?? string.Empty,
                    ["parentScreen"] = ReadString(destination, "parentScreen") ?? string.Empty,
                    ["synonyms"] = destination["synonyms"]?.DeepClone() ?? new JsonArray(),
                    ["purpose"] = ReadString(destination, "purpose") ?? string.Empty,
                    ["description"] = ReadString(destination, "description") ?? string.Empty,
                    ["scrollStatus"] = "unknown",
                    ["confidence"] = ReadDecimal(destination, "confidence", 0.8m)
                })
                .ToArray<JsonNode?>()),
            ["navigationHosts"] = navigationHosts.DeepClone(),
            ["tabGroups"] = tabGroups.DeepClone(),
            ["transitions"] = new JsonArray(transitions
                .OfType<JsonObject>()
                .Select(transition => new JsonObject
                {
                    ["id"] = ReadString(transition, "id") ?? string.Empty,
                    ["from"] = ReadString(transition, "from") ?? string.Empty,
                    ["to"] = ReadString(transition, "to") ?? string.Empty,
                    ["automationId"] = ReadString(transition["action"] as JsonObject, "automationId") ?? string.Empty,
                    ["semanticMeaning"] = ReadString(transition["action"] as JsonObject, "semanticMeaning") ?? string.Empty,
                    ["selector"] = transition["action"]?["selector"]?.DeepClone(),
                    ["confidence"] = ReadDecimal(transition["binding"] as JsonObject, "confidence", 0.8m)
                })
                .ToArray<JsonNode?>()),
            ["actions"] = new JsonArray(),
            ["coverage"] = new JsonObject
            {
                ["safeActionsObserved"] = 0,
                ["actionsExplored"] = 0,
                ["scrollContainersObserved"] = 0,
                ["scrollContainersCompleted"] = 0,
                ["gaps"] = new JsonArray()
            }
        };
        _ = ApplyReport(runId, report, turn);
    }

    public void Complete(string runId, string status, string message, DateTimeOffset completedUtc)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run))
            {
                return;
            }

            run.Status = status;
            run.Message = message;
            run.ActiveToolName = null;
            run.CompletedUtc = completedUtc;
            run.UpdatedUtc = completedUtc;
            database.Save(CreateSnapshot(run));
        }
    }

    private static bool TryReadDestination(
        JsonObject value,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveNode> existing,
        DateTimeOffset now,
        out SimulatorAgentAppGraphLiveNode? node,
        out string? error)
    {
        node = null;
        error = null;

        var id = NormalizeOptional(ReadString(value, "id"));
        var kind = NormalizeOptional(ReadString(value, "kind"))?.ToLowerInvariant();
        var name = NormalizeOptional(ReadString(value, "name"));
        var purpose = NormalizeOptional(ReadString(value, "purpose"));

        if (id is null || name is null || purpose is null || kind is null || !supportedKinds.Contains(kind))
        {
            error = "Each destination requires id, supported kind, name, and purpose.";

            return false;
        }

        var scrollStatus = NormalizeOptional(ReadString(value, "scrollStatus"))?.ToLowerInvariant()
            ?? "unknown";

        if (!supportedScrollStatuses.Contains(scrollStatus))
        {
            error = $"Destination '{id}' has unsupported scroll status '{scrollStatus}'.";

            return false;
        }

        existing.TryGetValue(id, out var previous);
        node = new SimulatorAgentAppGraphLiveNode(
            id,
            kind,
            name,
            NormalizeOptional(ReadString(value, "parentScreen")),
            ReadStringArray(value, "synonyms"),
            purpose,
            NormalizeOptional(ReadString(value, "description")) ?? previous?.Description ?? string.Empty,
            scrollStatus == "unknown" ? previous?.ScrollStatus ?? scrollStatus : scrollStatus,
            ClampConfidence(ReadDecimal(value, "confidence", previous?.Confidence ?? 0.8m)),
            previous?.FirstObservedUtc ?? now,
            now);

        return true;
    }

    private static bool TryReadTransition(
        JsonObject value,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveEdge> existing,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveNode> nodes,
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions,
        DateTimeOffset now,
        out SimulatorAgentAppGraphLiveEdge? edge,
        out string? error)
    {
        edge = null;
        error = null;

        var id = NormalizeOptional(ReadString(value, "id"));
        var from = NormalizeOptional(ReadString(value, "from"));
        var to = NormalizeOptional(ReadString(value, "to"));
        var semanticMeaning = NormalizeOptional(ReadString(value, "semanticMeaning"));
        var missingIdentity = id is null
            || from is null
            || to is null
            || semanticMeaning is null;

        if (missingIdentity)
        {
            error = "Each transition requires id, from, to, and semanticMeaning.";

            return false;
        }

        existing.TryGetValue(id!, out var reportedPrevious);

        var reportedSelector = reportedPrevious is not null && reportedPrevious.From == from
            ? reportedPrevious.Selector
            : null;
        var selector = AppGraphActionIdentity.NormalizeSelector(
            value["selector"] as JsonObject ?? reportedSelector);
        var automationId = NormalizeOptional(ReadString(value, "automationId"))
            ?? NormalizeOptional(ReadString(selector, "automationId"))
            ?? string.Empty;

        if (automationId.Length > 0)
        {
            selector["automationId"] = automationId;
        }

        var matches = existing.Values
            .Where(candidate => HasSameTransitionAction(candidate, from!, automationId, selector))
            .ToArray();
        var previous = matches.FirstOrDefault(candidate => candidate.To == to);
        var matchesLegacyUnidentifiedTransition = reportedPrevious is not null
            && reportedPrevious.From == from
            && reportedPrevious.To == to
            && automationId.Length == 0
            && selector.Count == 0;
        var hasSameReportedIdentity = reportedPrevious is not null
            && (HasSameTransitionAction(reportedPrevious, from!, automationId, selector)
                || matchesLegacyUnidentifiedTransition);
        var action = FindMatchingAction(actions, from!, "ansight_tap_ui", automationId, selector);
        var navigationKind = AppGraphActionIdentity.NavigationKind(
            "ansight_tap_ui", automationId, selector,
            NormalizeOptional(ReadString(value, "navigationKind"))
                ?? action?.NavigationKind
                ?? previous?.NavigationKind
                ?? (hasSameReportedIdentity ? reportedPrevious?.NavigationKind : null));
        var isReturn = navigationKind is "back" or "pop" or "dismiss";

        if (!isReturn && previous is null)
        {
            var owningTransition = matches.FirstOrDefault(candidate => IsDestinationWithin(nodes, to!, candidate.To));

            if (owningTransition is not null)
            {
                // Entering a known screen can restore one of its internal states.
                // Preserve the proven entry route; the current node and action outcome
                // still retain the exact observed child state.
                to = owningTransition.To;
                previous = owningTransition;
            }
        }

        var conflictingDestination = matches.FirstOrDefault(candidate => candidate.To != to);

        if (!isReturn && conflictingDestination is not null)
        {
            error = $"Action '{automationId}' from '{from}' already leads to '{conflictingDestination.To}'. "
                + "A different destination requires a distinct observed state or return-navigation context.";

            return false;
        }

        var reportedReturnDestination = ReadString(value["navigationContext"] as JsonObject, "returnDestinationId");
        var hasInvalidReturnContext = reportedReturnDestination is not null
            && (!isReturn || reportedReturnDestination != to);

        if (hasInvalidReturnContext)
        {
            error = "Return-navigation context must identify this return transition's destination.";

            return false;
        }

        var needsDistinctTransitionId = reportedPrevious is not null
            && (!hasSameReportedIdentity || reportedPrevious.To != to);

        if (previous is not null)
        {
            id = previous.Id;
        }
        else if (needsDistinctTransitionId)
        {
            if (hasSameReportedIdentity && !isReturn)
            {
                error = $"Transition '{id}' cannot replace its previously observed destination.";

                return false;
            }

            id = FindUnusedId(BuildTransitionId(from!, to!, automationId, selector), existing.Keys);
        }
        else
        {
            previous = reportedPrevious;
        }

        edge = new SimulatorAgentAppGraphLiveEdge(
            id!,
            from!,
            to!,
            automationId,
            semanticMeaning!,
            ClampConfidence(ReadDecimal(value, "confidence", previous?.Confidence ?? 0.8m)),
            previous?.FirstObservedUtc ?? now,
            now,
            selector,
            navigationKind,
            isReturn ? new SimulatorAgentAppGraphNavigationContext(to!) : null);

        return true;
    }

    private static bool HasSameTransitionAction(
        SimulatorAgentAppGraphLiveEdge edge,
        string from,
        string automationId,
        JsonObject? selector)
    {
        if (edge.From != from)
        {
            return false;
        }

        return automationId.Length > 0
            ? edge.AutomationId == automationId
            : AppGraphActionIdentity.HasObservedSelector(selector)
                && JsonNode.DeepEquals(AppGraphActionIdentity.NormalizeSelector(edge.Selector), selector);
    }

    private static string BuildTransitionId(string from, string to, string automationId, JsonObject selector)
    {
        var identity = new JsonObject
        {
            ["from"] = from,
            ["to"] = to,
            ["automationId"] = automationId,
            ["selector"] = selector.DeepClone()
        };
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToJsonString()));

        return $"edge_{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private static bool TryReadNavigationHost(
        JsonObject value,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveNavigationHost> existing,
        DateTimeOffset now,
        out SimulatorAgentAppGraphLiveNavigationHost? navigationHost,
        out string? error)
    {
        navigationHost = null;
        error = null;

        var id = NormalizeOptional(ReadString(value, "id"));
        var kind = NormalizeOptional(ReadString(value, "kind"))?.ToLowerInvariant();
        var name = NormalizeOptional(ReadString(value, "name"));
        var destinationId = NormalizeOptional(ReadString(value, "destinationId"));
        var activeChildDestinationId = NormalizeOptional(
            ReadString(value, "activeChildDestinationId"));
        var childDestinationIds = ReadStringArray(value, "childDestinationIds");

        if (id is null
            || kind is null
            || !supportedNavigationHostKinds.Contains(kind)
            || name is null
            || destinationId is null
            || activeChildDestinationId is null
            || childDestinationIds.Count == 0)
        {
            error = "Each navigation host requires id, supported kind, name, destinationId, "
                + "activeChildDestinationId, and at least one childDestinationId.";

            return false;
        }

        existing.TryGetValue(id, out var previous);

        AppGraphNavigationTechnologyDescriptor? technology = previous?.Technology;

        if (value["technology"] is JsonObject technologyValue)
        {
            if (!AppGraphNavigationTechnologyCatalog.TryReadAndValidate(
                technologyValue,
                AppGraphNavigationTechnologyCatalog.NavigationHostScope,
                out technology,
                out var normalizedRole,
                out var technologyError))
            {
                error = technologyError;

                return false;
            }

            if (!string.Equals(kind, normalizedRole, StringComparison.Ordinal))
            {
                error = $"Navigation host '{id}' kind must be '{normalizedRole}' for technology '{technology!.Framework}/{technology.Kind}'.";

                return false;
            }
        }

        if (!AppGraphNavigationTechnologyCatalog.TryValidateChildOrder(
            technology, childDestinationIds, activeChildDestinationId, out var orderError))
        {
            error = orderError;

            return false;
        }

        navigationHost = new SimulatorAgentAppGraphLiveNavigationHost(
            id,
            kind,
            name,
            destinationId,
            activeChildDestinationId,
            childDestinationIds,
            technology?.Framework
                ?? NormalizeOptional(ReadString(value, "framework"))
                ?? previous?.Framework
                ?? "unknown",
            ClampConfidence(ReadDecimal(value, "confidence", previous?.Confidence ?? 0.8m)),
            previous?.FirstObservedUtc ?? now,
            now,
            technology);

        return true;
    }

    private static bool TryReadTabGroup(
        JsonObject value,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveTabGroup> existing,
        DateTimeOffset now,
        out SimulatorAgentAppGraphLiveTabGroup? tabGroup,
        out string? error)
    {
        tabGroup = null;
        error = null;

        var id = NormalizeOptional(ReadString(value, "id"));
        var parentDestinationId = NormalizeOptional(ReadString(value, "parentDestinationId"));
        var selectedDestinationId = NormalizeOptional(ReadString(value, "selectedDestinationId"));
        var tabDestinationIds = ReadStringArray(value, "tabDestinationIds");

        if (id is null
            || parentDestinationId is null
            || selectedDestinationId is null
            || tabDestinationIds.Count == 0)
        {
            error = "Each tab group requires id, parentDestinationId, selectedDestinationId, "
                + "and at least one tabDestinationId.";

            return false;
        }

        existing.TryGetValue(id, out var previous);

        var duplicate = existing.Values.FirstOrDefault(group => group.Id != id
            && group.ParentDestinationId == parentDestinationId
            && group.TabDestinationIds.SequenceEqual(tabDestinationIds));

        if (duplicate is not null)
        {
            error = $"This tab group is already tracked as '{duplicate.Id}'. Update that canonical group.";

            return false;
        }

        AppGraphNavigationTechnologyDescriptor? technology = previous?.Technology;

        if (value["technology"] is JsonObject technologyValue
            && !AppGraphNavigationTechnologyCatalog.TryReadAndValidate(
                technologyValue,
                AppGraphNavigationTechnologyCatalog.TabGroupScope,
                out technology,
                out _,
                out error))
        {
            return false;
        }

        tabGroup = new SimulatorAgentAppGraphLiveTabGroup(
            id,
            parentDestinationId,
            selectedDestinationId,
            tabDestinationIds,
            ClampConfidence(ReadDecimal(value, "confidence", previous?.Confidence ?? 0.8m)),
            previous?.FirstObservedUtc ?? now,
            now,
            technology);

        return true;
    }

    private static void ApplyStructureParents(MutableRun run, DateTimeOffset now)
    {
        foreach (var navigationHost in run.NavigationHosts.Values)
        {
            foreach (var childDestinationId in navigationHost.ChildDestinationIds)
            {
                if (run.Nodes.TryGetValue(childDestinationId, out var child)
                    && !string.Equals(
                        child.Id,
                        navigationHost.DestinationId,
                        StringComparison.Ordinal))
                {
                    run.Nodes[child.Id] = child with
                    {
                        ParentScreen = navigationHost.DestinationId,
                        UpdatedUtc = now
                    };
                }
            }
        }

        foreach (var tabGroup in run.TabGroups.Values)
        {
            foreach (var tabDestinationId in tabGroup.TabDestinationIds)
            {
                if (run.Nodes.TryGetValue(tabDestinationId, out var tab))
                {
                    run.Nodes[tab.Id] = tab with
                    {
                        ParentScreen = tabGroup.ParentDestinationId,
                        UpdatedUtc = now
                    };
                }
            }
        }
    }

    private static bool TryReadAction(
        JsonObject value,
        IReadOnlyDictionary<string, SimulatorAgentAppGraphLiveAction> existing,
        DateTimeOffset now,
        out SimulatorAgentAppGraphLiveAction? action,
        out string? error)
    {
        action = null;
        error = null;

        var id = NormalizeOptional(ReadString(value, "id"));
        var destinationId = NormalizeOptional(ReadString(value, "destinationId"));
        var toolName = NormalizeOptional(ReadString(value, "toolName"));
        var automationId = NormalizeOptional(ReadString(value, "automationId")) ?? string.Empty;
        var semanticMeaning = NormalizeOptional(ReadString(value, "semanticMeaning"));
        var status = NormalizeOptional(ReadString(value, "status"))?.ToLowerInvariant();
        var hasInvalidIdentityOrStatus = id is null
            || destinationId is null
            || toolName is null
            || semanticMeaning is null
            || status is null
            || !supportedActionStatuses.Contains(status);

        if (hasInvalidIdentityOrStatus)
        {
            error = "Each action requires id, destinationId, toolName, semanticMeaning, and a supported status.";

            return false;
        }

        existing.TryGetValue(id!, out var previous);

        var canReuseReportedSelector = previous is not null
            && previous.DestinationId == destinationId
            && previous.ToolName == toolName;
        var selector = AppGraphActionIdentity.NormalizeSelector(
            value["selector"] as JsonObject ?? (canReuseReportedSelector ? previous?.Selector : null));

        if (automationId.Length == 0)
        {
            automationId = NormalizeOptional(ReadString(selector, "automationId")) ?? string.Empty;
        }

        if (automationId.Length > 0)
        {
            selector["automationId"] = automationId;
        }

        var matchingAction = FindMatchingAction(existing.Values, destinationId!, toolName!, automationId, selector);
        var previousMatchesIdentity = previous is not null
            && previous.DestinationId == destinationId
            && previous.ToolName == toolName
            && (automationId.Length > 0
                ? previous.AutomationId == automationId
                : JsonNode.DeepEquals(AppGraphActionIdentity.NormalizeSelector(previous.Selector), selector));

        if (previousMatchesIdentity && matchingAction?.Status == "unavailable")
        {
            matchingAction = previous;
        }

        var isExplicitRetirement = status == "unavailable"
            && !string.IsNullOrWhiteSpace(ReadString(value, "lastOutcome"));

        if (matchingAction is not null && !isExplicitRetirement)
        {
            id = matchingAction.Id;
            previous = matchingAction;
        }
        else if (previous is not null && !previousMatchesIdentity)
        {
            id = FindUnusedId(BuildActionId(destinationId!, toolName!, selector), existing.Keys);
            previous = null;
        }

        var navigationKind = AppGraphActionIdentity.NavigationKind(toolName!, automationId, selector,
            NormalizeOptional(ReadString(value, "navigationKind")) ?? previous?.NavigationKind);

        if (status == "navigation" && navigationKind.Length == 0)
        {
            error = $"Action '{id}' needs a back, pop, dismiss, tab, or menu navigationKind to use navigation status.";

            return false;
        }

        if (navigationKind.Length > 0 && status is not ("unsafe" or "blocked" or "unavailable"))
        {
            status = "navigation";
        }

        var recheckReason = NormalizeOptional(ReadString(value, "recheckReason"));
        var explicitlyRechecksAction = IsExplicitRecheck(previous, status, recheckReason);
        var isStaleDiscoveryStatus = previous is not null
            && terminalActionStatuses.Contains(previous.Status)
            && !explicitlyRechecksAction
            && status is "queued" or "attempted" or "needs_verification" or "deferred";

        if (isStaleDiscoveryStatus)
        {
            status = previous!.Status;
        }

        var restoresNavigation = previous?.Status == "unavailable"
            && status == "navigation"
            && !string.IsNullOrWhiteSpace(ReadString(value, "lastOutcome"));
        var changesTerminalStatus = previous is not null
            && terminalActionStatuses.Contains(previous.Status)
            && !(navigationKind.Length > 0 && previous.Status == "explored")
            && !restoresNavigation
            && !explicitlyRechecksAction
            && !string.Equals(previous.Status, status, StringComparison.Ordinal);

        if (changesTerminalStatus)
        {
            error = $"Terminal action '{id}' cannot move from '{previous!.Status}' back to '{status}'.";

            return false;
        }

        action = new SimulatorAgentAppGraphLiveAction(
            id!,
            destinationId!,
            toolName!,
            automationId,
            selector,
            semanticMeaning!,
            status!,
            previous?.AttemptCount ?? 0,
            explicitlyRechecksAction ? $"Explicit recheck: {recheckReason}. Previous outcome: {previous!.LastOutcome}"
                : NormalizeOptional(ReadString(value, "lastOutcome")) ?? previous?.LastOutcome ?? string.Empty,
            NormalizeOptional(ReadString(value, "resultDestinationId")) ?? previous?.ResultDestinationId,
            previous?.FirstObservedUtc ?? now,
            now,
            navigationKind)
        {
            RecheckAttemptBaseline = explicitlyRechecksAction
                ? previous!.AttemptCount : previous?.RecheckAttemptBaseline ?? 0,
            LastRecheckReason = explicitlyRechecksAction
                ? recheckReason! : previous?.LastRecheckReason ?? string.Empty
        };

        return true;
    }

    private static bool IsExplicitRecheck(
        SimulatorAgentAppGraphLiveAction? previous, string? status, string? reason)
    {
        var hasUnresolvedAttempt = previous?.Status is "blocked" or "attempted" or "deferred"
            || previous is { Status: "queued", AttemptCount: > 0 };
        var explicitlyRechecks = hasUnresolvedAttempt
            && status is "queued" or "navigation"
            && reason is not null
            && !string.Equals(reason, previous?.LastRecheckReason, StringComparison.OrdinalIgnoreCase);

        return explicitlyRechecks;
    }

    private static SimulatorAgentAppGraphLiveCoverage MergeCoverage(
        SimulatorAgentAppGraphLiveCoverage current,
        JsonObject value)
    {
        return new SimulatorAgentAppGraphLiveCoverage(
            Math.Max(current.SafeActionsObserved, ReadNonNegativeInt(value, "safeActionsObserved")),
            Math.Max(current.ActionsExplored, ReadNonNegativeInt(value, "actionsExplored")),
            Math.Max(current.ScrollContainersObserved, ReadNonNegativeInt(value, "scrollContainersObserved")),
            Math.Max(current.ScrollContainersCompleted, ReadNonNegativeInt(value, "scrollContainersCompleted")),
            current.Gaps
                .Concat(ReadStringArray(value, "gaps"))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static SimulatorAgentAppGraphLiveCoverage DeriveCoverage(
        MutableRun run,
        SimulatorAgentAppGraphLiveCoverage legacyCoverage)
    {
        var safeActionsObserved = legacyCoverage.SafeActionsObserved;
        var actionsExplored = legacyCoverage.ActionsExplored;

        if (run.Actions.Count > 0)
        {
            safeActionsObserved = run.Actions.Values.Count(static action =>
                        !action.IsNavigationEssential &&
                        action.Status is not ("unsafe" or "unavailable"));
            actionsExplored = run.Actions.Values.Count(static action =>
                !action.IsNavigationEssential && action.Status == "explored");
        }

        var scrollContainersObserved = run.Nodes.Count > 0
            ? run.Nodes.Count
            : legacyCoverage.ScrollContainersObserved;
        var scrollContainersCompleted = run.Nodes.Count > 0
            ? run.Nodes.Values.Count(static node =>
                node.ScrollStatus is "not_scrollable" or "complete")
            : Math.Min(
                legacyCoverage.ScrollContainersCompleted,
                legacyCoverage.ScrollContainersObserved);

        return new SimulatorAgentAppGraphLiveCoverage(
            safeActionsObserved,
            Math.Min(actionsExplored, safeActionsObserved),
            scrollContainersObserved,
            Math.Min(scrollContainersCompleted, scrollContainersObserved),
            legacyCoverage.Gaps);
    }

    private static void MarkTransitionActionExplored(
        MutableRun run,
        SimulatorAgentAppGraphLiveEdge edge,
        DateTimeOffset now)
    {
        var action = run.Actions.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.DestinationId, edge.From, StringComparison.Ordinal)
                    && (edge.AutomationId.Length > 0
                        ? string.Equals(candidate.AutomationId, edge.AutomationId, StringComparison.Ordinal)
                        : AppGraphActionIdentity.HasObservedSelector(edge.Selector) && JsonNode.DeepEquals(candidate.Selector, edge.Selector)));

        if (action is null)
        {
            return;
        }

        run.Actions[action.Id] = action with
        {
            Status = action.IsNavigationEssential ? "navigation" : "explored",
            LastOutcome = $"Verified transition to '{edge.To}'.",
            ResultDestinationId = edge.To,
            UpdatedUtc = now
        };
    }

    private static SimulatorAgentAppGraphLiveAction? FindMatchingAction(
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions,
        string destinationId,
        string toolName,
        string automationId,
        JsonObject? selector = null)
    {
        var normalizedAutomationId = NormalizeOptional(automationId) ?? string.Empty;

        return actions.OrderBy(action => action.Status == "unavailable").FirstOrDefault(action =>
            string.Equals(action.DestinationId, destinationId, StringComparison.Ordinal)
                && string.Equals(action.ToolName, toolName, StringComparison.Ordinal)
                && (normalizedAutomationId.Length > 0
                    ? string.Equals(action.AutomationId, normalizedAutomationId, StringComparison.Ordinal)
                    : selector is not null && (AppGraphActionIdentity.HasObservedSelector(selector) || toolName == "ansight_back_ui")
                        && JsonNode.DeepEquals(action.Selector, AppGraphActionIdentity.NormalizeSelector(selector))));
    }

    private static IReadOnlyList<string> BuildStructureGaps(MutableRun run)
    {
        var gaps = new List<string>();

        foreach (var action in run.Actions.Values)
        {
            if (!run.Nodes.ContainsKey(action.DestinationId))
            {
                gaps.Add($"Action '{action.Id}' is missing destination '{action.DestinationId}'.");
            }

            if (action.ResultDestinationId is { } result && !run.Nodes.ContainsKey(result))
            {
                gaps.Add($"Action '{action.Id}' is missing result destination '{result}'.");
            }
        }

        foreach (var edge in run.Edges.Values)
        {
            if (!run.Nodes.ContainsKey(edge.From) || !run.Nodes.ContainsKey(edge.To))
            {
                gaps.Add($"Transition '{edge.Id}' references an unknown destination.");
            }
        }

        foreach (var navigationHost in run.NavigationHosts.Values)
        {
            if (!run.Nodes.ContainsKey(navigationHost.DestinationId))
            {
                gaps.Add($"Navigation host '{navigationHost.Id}' is missing destination '{navigationHost.DestinationId}'.");
            }

            if (!AppGraphNavigationTechnologyCatalog.TryValidateChildOrder(
                navigationHost.Technology, navigationHost.ChildDestinationIds,
                navigationHost.ActiveChildDestinationId, out var orderError))
            {
                gaps.Add(orderError);
            }

            if (navigationHost.ChildDestinationIds.Count == 0)
            {
                gaps.Add($"Navigation host '{navigationHost.Id}' has no observed children; inventory its direct destinations.");
            }

            if (!navigationHost.ChildDestinationIds.Contains(
                navigationHost.ActiveChildDestinationId,
                StringComparer.Ordinal))
            {
                gaps.Add($"Navigation host '{navigationHost.Id}' does not include active child '{navigationHost.ActiveChildDestinationId}'.");
            }

            foreach (var childDestinationId in navigationHost.ChildDestinationIds)
            {
                if (!run.Nodes.ContainsKey(childDestinationId))
                {
                    gaps.Add($"Navigation host '{navigationHost.Id}' is missing child destination '{childDestinationId}'.");
                }
            }
        }

        foreach (var tabGroup in run.TabGroups.Values)
        {
            if (!run.Nodes.ContainsKey(tabGroup.ParentDestinationId))
            {
                gaps.Add($"Tab group '{tabGroup.Id}' is missing parent destination '{tabGroup.ParentDestinationId}'.");
            }

            if (tabGroup.TabDestinationIds.Count == 0)
            {
                gaps.Add($"Tab group '{tabGroup.Id}' has no observed tabs; record the selected tab and every visible sibling before navigating.");
            }

            if (!tabGroup.TabDestinationIds.Contains(
                tabGroup.SelectedDestinationId,
                StringComparer.Ordinal))
            {
                gaps.Add($"Tab group '{tabGroup.Id}' does not include selected tab '{tabGroup.SelectedDestinationId}'.");
            }

            foreach (var tabDestinationId in tabGroup.TabDestinationIds)
            {
                if (!run.Nodes.ContainsKey(tabDestinationId))
                {
                    gaps.Add($"Tab group '{tabGroup.Id}' is missing tab destination '{tabDestinationId}'.");
                }
            }
        }

        gaps.AddRange(AppGraphContainment.FindErrors(
            run.NavigationHosts.Values.SelectMany(host => host.ChildDestinationIds.Select(child =>
                new AppGraphContainmentRelation(host.DestinationId, child)))
                .Concat(run.TabGroups.Values.SelectMany(group => group.TabDestinationIds.Select(child =>
                new AppGraphContainmentRelation(group.ParentDestinationId, child))))));

        return gaps.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string BuildFrontierGuidance(MutableRun run)
    {
        var pending = AppGraphFrontier.Pending(run.Actions.Values, run.Nodes.Values, run.CurrentDestinationId);

        if (pending.Count == 0)
        {
            return ReadPrompt("action-frontier-complete");
        }

        var next = pending[0];

        return RenderPrompt(
            "action-frontier-next",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ACTION_ID"] = next.Id,
                ["DESTINATION_ID"] = next.DestinationId,
                ["TOOL_NAME"] = next.ToolName,
                ["AUTOMATION_ID"] = next.AutomationId
            });
    }

    private static string ReadPrompt(string name)
        => EmbeddedTextResource.ReadSection(
            "SimulatorAgent/Prompts/app-graph.md",
            name);

    private static string RenderPrompt(
        string name,
        IReadOnlyDictionary<string, string> values)
        => EmbeddedTextResource.RenderSection(
            "SimulatorAgent/Prompts/app-graph.md",
            name,
            values).TrimEnd();

    private static string BuildActionId(
        string destinationId,
        string toolName,
        JsonObject selector)
    {
        var source = $"{destinationId}\n{toolName}\n{selector.ToJsonString()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant();

        return $"action_{hash[..16]}";
    }

    private static string FindUnusedId(string proposedId, IEnumerable<string> existingIds)
    {
        var usedIds = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var id = proposedId;
        var suffix = 2;

        while (usedIds.Contains(id))
        {
            id = $"{proposedId}_{suffix}";
            suffix++;
        }

        return id;
    }

    private static string NormalizeIdentity(string? value)
        => string.Concat((value ?? string.Empty)
            .Where(static character => char.IsLetterOrDigit(character)))
            .ToLowerInvariant();

    private static SimulatorAgentAppGraphLiveRun CreateSnapshot(MutableRun run)
        => new(
            "ansight.app-graph-live/v3",
            run.RunId,
            run.SessionId,
            run.AppId,
            run.GraphName,
            run.Status,
            run.Message,
            run.CurrentDestinationId,
            run.ActiveToolName,
            run.Turn,
            run.StartedUtc,
            run.UpdatedUtc,
            run.CompletedUtc,
            run.Coverage with
            {
                Gaps = run.Coverage.Gaps.ToArray()
            },
            run.Nodes.Values.OrderBy(static node => node.FirstObservedUtc).ToArray(),
            run.Edges.Values.OrderBy(static edge => edge.FirstObservedUtc).ToArray(),
            run.NavigationHosts.Values
                .OrderBy(static navigationHost => navigationHost.FirstObservedUtc)
                .ToArray(),
            run.TabGroups.Values
                .OrderBy(static tabGroup => tabGroup.FirstObservedUtc)
                .ToArray(),
            run.Actions.Values
                .OrderBy(static action => action.FirstObservedUtc)
                .Select(static action => action with
                {
                    Selector = action.Selector.DeepClone().AsObject()
                })
                .ToArray(),
            run.Trace.ToArray())
        {
            PendingOutcome = run.PendingOutcome
        };

    private void TrimRetainedRuns()
    {
        foreach (var runId in runs.Values
            .OrderByDescending(static run => run.UpdatedUtc)
            .Skip(MaximumRetainedRuns)
            .Select(static run => run.RunId)
            .ToArray())
        {
            runs.Remove(runId);
        }
    }

    private static string? ReadString(JsonObject? value, string propertyName)
        => value?[propertyName] is JsonValue stringValue
            && stringValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonObject value, string propertyName)
        => value[propertyName] is JsonArray array
            ? array
                .OfType<JsonValue>()
                .Select(static item => item.TryGetValue<string>(out var text) ? NormalizeOptional(text) : null)
                .Where(static text => text is not null)
                .Select(static text => text!)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static decimal ReadDecimal(JsonObject? value, string propertyName, decimal fallback)
    {
        if (value?[propertyName] is not JsonValue number)
        {
            return fallback;
        }

        if (number.TryGetValue<decimal>(out var decimalValue))
        {
            return decimalValue;
        }

        if (number.TryGetValue<double>(out var doubleValue))
        {
            return (decimal)doubleValue;
        }

        if (number.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return fallback;
    }

    private static int ReadNonNegativeInt(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue number
            && number.TryGetValue<int>(out var result)
            ? Math.Max(0, result)
            : 0;

    private static decimal ClampConfidence(decimal value) => Math.Clamp(value, 0m, 1m);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateReportReferences(MutableRun run, JsonObject report)
    {
        // Only validate supplied records here, so an older incomplete graph can be
        // repaired incrementally. Completion checks inspect the entire graph.
        var fields = new Dictionary<string, string[]>
        {
            ["destinations"] = ["parentScreen"],
            ["transitions"] = ["from", "to"],
            ["actions"] = ["destinationId", "resultDestinationId"],
            ["navigationHosts"] = ["destinationId", "activeChildDestinationId", "childDestinationIds"],
            ["tabGroups"] = ["parentDestinationId", "selectedDestinationId", "tabDestinationIds"]
        };

        foreach (var collection in fields)
        {
            foreach (var record in (report[collection.Key] as JsonArray ?? []).OfType<JsonObject>())
            {
                foreach (var field in collection.Value)
                {
                    var references = record[field] is JsonArray ? ReadStringArray(record, field)
                        : new[] { ReadString(record, field) ?? string.Empty };

                    foreach (var reference in references.Where(value => !string.IsNullOrWhiteSpace(value)))
                    {
                        if (!run.Nodes.ContainsKey(reference))
                        {
                            return $"{collection.Key} record '{ReadString(record, "id")}' references unknown destination '{reference}' in {field}. "
                                + "Reuse a retained destination ID or include its observed destination in this report. No changes were applied.";
                        }
                    }
                }
            }
        }

        if (run.CurrentDestinationId is { } current && !run.Nodes.ContainsKey(current))
        {
            return $"currentDestinationId '{current}' is unknown. Report the observed destination first. No changes were applied.";
        }

        return null;
    }

    private sealed class MutableRun
    {
        public MutableRun(
            string runId,
            string sessionId,
            string? appId,
            string graphName,
            DateTimeOffset startedUtc)
        {
            RunId = runId;
            SessionId = sessionId;
            AppId = appId;
            GraphName = graphName;
            StartedUtc = startedUtc;
            UpdatedUtc = startedUtc;
        }

        public string RunId { get; }
        public string SessionId { get; set; }
        public string? AppId { get; }
        public string GraphName { get; }
        public string Status { get; set; } = "starting";

        public string Message { get; set; } = "Preparing exhaustive App Graph exploration.";

        public string? CurrentDestinationId { get; set; }
        public string? ActiveToolName { get; set; }
        public int Turn { get; set; }
        public DateTimeOffset StartedUtc { get; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public SimulatorAgentAppGraphLiveCoverage Coverage { get; set; } = SimulatorAgentAppGraphLiveCoverage.Empty;

        public Dictionary<string, SimulatorAgentAppGraphLiveNode> Nodes { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SimulatorAgentAppGraphLiveEdge> Edges { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SimulatorAgentAppGraphLiveNavigationHost> NavigationHosts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SimulatorAgentAppGraphLiveTabGroup> TabGroups { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, SimulatorAgentAppGraphLiveAction> Actions { get; } = new(StringComparer.Ordinal);

        public SimulatorAgentAppGraphPendingOutcome? PendingOutcome { get; set; }
        public long TraceSequence { get; set; }
        public List<SimulatorAgentAppGraphLiveTraceEntry> Trace { get; } = [];

        public MutableRun CopyForReport()
        {
            var copy = new MutableRun(RunId, SessionId, AppId, GraphName, StartedUtc)
            {
                Status = Status,
                Message = Message,
                CurrentDestinationId = CurrentDestinationId,
                ActiveToolName = ActiveToolName,
                Turn = Turn,
                UpdatedUtc = UpdatedUtc,
                CompletedUtc = CompletedUtc,
                Coverage = Coverage,
                TraceSequence = TraceSequence,
                PendingOutcome = PendingOutcome
            };

            foreach (var entry in Nodes)
            {
                copy.Nodes.Add(entry.Key, entry.Value);
            }

            foreach (var entry in Edges)
            {
                copy.Edges.Add(entry.Key, entry.Value);
            }

            foreach (var entry in NavigationHosts)
            {
                copy.NavigationHosts.Add(entry.Key, entry.Value);
            }

            foreach (var entry in TabGroups)
            {
                copy.TabGroups.Add(entry.Key, entry.Value);
            }

            foreach (var entry in Actions)
            {
                copy.Actions.Add(entry.Key, entry.Value);
            }

            copy.Trace.AddRange(Trace);

            return copy;
        }
    }
}
