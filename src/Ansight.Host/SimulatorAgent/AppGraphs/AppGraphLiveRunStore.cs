using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed class AppGraphLiveRunStore
{
    private const int MaximumRetainedRuns = 12;
    private const int MaximumRetainedTraceEntries = 12_288;
    private static readonly HashSet<string> supportedKinds =
        new(["screen", "dialog", "state"], StringComparer.Ordinal);
    private static readonly HashSet<string> supportedScrollStatuses =
        new(["unknown", "not_scrollable", "in_progress", "complete", "blocked"], StringComparer.Ordinal);
    private static readonly HashSet<string> supportedActionStatuses =
        new(["queued", "attempted", "explored", "blocked", "unsafe", "unavailable"], StringComparer.Ordinal);
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
                foreach (var tabGroup in resumeState.TabGroups)
                {
                    run.TabGroups[tabGroup.Id] = tabGroup;
                }
                foreach (var action in resumeState.Actions)
                {
                    run.Actions[action.Id] = action with
                    {
                        Selector = action.Selector.DeepClone().AsObject()
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
            return runs.Values
                .Where(run => string.IsNullOrWhiteSpace(sessionId)
                              || string.Equals(run.SessionId, sessionId.Trim(), StringComparison.Ordinal))
                .OrderByDescending(static run => run.UpdatedUtc)
                .Select(CreateSnapshot)
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

            var now = DateTimeOffset.UtcNow;
            var destinationCount = 0;
            foreach (var value in destinationValues)
            {
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

                var matchingAction = action!.AutomationId.Length == 0
                    ? null
                    : FindMatchingAction(
                        run.Actions.Values,
                        action.DestinationId,
                        action.ToolName,
                        action.AutomationId);
                if (matchingAction is not null
                    && !string.Equals(matchingAction.Id, action.Id, StringComparison.Ordinal))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        $"Action '{action.AutomationId}' from destination "
                        + $"'{action.DestinationId}' is already tracked as '{matchingAction.Id}'. "
                        + "Update the existing canonical action instead of creating an alias.");
                }

                run.Actions[action.Id] = action;
                actionCount++;
            }

            var transitionCount = 0;
            foreach (var value in transitionValues)
            {
                SimulatorAgentAppGraphLiveEdge? edge = null;
                string? error = null;
                if (value is not JsonObject transition
                    || !TryReadTransition(transition, run.Edges, now, out edge, out error))
                {
                    return AppGraphLiveUpdateResult.Failure(
                        error ?? "A transition update was invalid.");
                }

                run.Edges[edge!.Id] = edge;
                MarkTransitionActionExplored(run, edge, now);
                transitionCount++;
            }

            run.CurrentDestinationId = NormalizeOptional(ReadString(arguments, "currentDestinationId"));
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
            return AppGraphLiveUpdateResult.Success(
                destinationCount,
                transitionCount,
                actionCount,
                navigationHostCount,
                tabGroupCount,
                run.Nodes.Count,
                run.Edges.Count,
                BuildPendingActions(run.Actions.Values));
        }
    }

    public void RecordActionAttempt(
        string runId,
        string destinationId,
        string toolName,
        JsonObject selector,
        bool succeeded,
        string outcome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(selector);
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
                automationId);
            var now = DateTimeOffset.UtcNow;
            var id = existing?.Id ?? BuildActionId(destinationId, toolName, selector);
            var status = existing is not null && terminalActionStatuses.Contains(existing.Status)
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
                now);
            run.Coverage = DeriveCoverage(run, run.Coverage);
            run.UpdatedUtc = now;
            database.Save(CreateSnapshot(run));
        }
    }

    public bool TryBuildActionGuardMessage(
        string runId,
        string destinationId,
        string toolName,
        string automationId,
        int maximumAttempts,
        out string message)
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
                automationId);
            if (action is null)
            {
                message = string.Empty;
                return false;
            }

            var guidance = BuildFrontierGuidance(run.Actions.Values);
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

            if (action.AttemptCount >= maximumAttempts)
            {
                message = RenderPrompt(
                    "action-guard-attempted",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AUTOMATION_ID"] = action.AutomationId,
                        ["DESTINATION_ID"] = action.DestinationId,
                        ["ATTEMPT_COUNT"] = action.AttemptCount.ToString(),
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

        var duplicate = existing.Values.FirstOrDefault(candidate =>
            !string.Equals(candidate.Id, id, StringComparison.Ordinal)
            && string.Equals(candidate.Kind, kind, StringComparison.Ordinal)
            && string.Equals(NormalizeIdentity(candidate.Name), NormalizeIdentity(name), StringComparison.Ordinal)
            && string.Equals(
                NormalizeIdentity(candidate.ParentScreen),
                NormalizeIdentity(ReadString(value, "parentScreen")),
                StringComparison.Ordinal));
        if (duplicate is not null)
        {
            error = $"Destination '{name}' is already tracked as '{duplicate.Id}'. "
                    + "Update the existing canonical destination instead of creating an alias.";
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
        if (id is null || from is null || to is null || semanticMeaning is null)
        {
            error = "Each transition requires id, from, to, and semanticMeaning.";
            return false;
        }

        var automationId = NormalizeOptional(ReadString(value, "automationId")) ?? string.Empty;
        if (automationId.Length > 0)
        {
            var duplicate = existing.Values.FirstOrDefault(candidate =>
                !string.Equals(candidate.Id, id, StringComparison.Ordinal)
                && string.Equals(candidate.From, from, StringComparison.Ordinal)
                && string.Equals(candidate.AutomationId, automationId, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                error = string.Equals(duplicate.To, to, StringComparison.Ordinal)
                    ? $"Action '{automationId}' from '{from}' is already recorded as transition '{duplicate.Id}'. "
                      + "Update the existing canonical transition instead of creating an alias."
                    : $"Action '{automationId}' from '{from}' already leads to '{duplicate.To}'. "
                      + $"The conflicting destination '{to}' must be canonicalized before reporting this transition.";
                return false;
            }
        }

        existing.TryGetValue(id, out var previous);
        edge = new SimulatorAgentAppGraphLiveEdge(
            id,
            from,
            to,
            automationId,
            semanticMeaning,
            ClampConfidence(ReadDecimal(value, "confidence", previous?.Confidence ?? 0.8m)),
            previous?.FirstObservedUtc ?? now,
            now);
        return true;
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
            previous?.SelectedDestinationId ?? selectedDestinationId,
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
        if (id is null
            || destinationId is null
            || toolName is null
            || semanticMeaning is null
            || status is null
            || !supportedActionStatuses.Contains(status))
        {
            error = "Each action requires id, destinationId, toolName, semanticMeaning, and a supported status.";
            return false;
        }

        existing.TryGetValue(id, out var previous);
        if (previous is not null
            && terminalActionStatuses.Contains(previous.Status)
            && !string.Equals(previous.Status, status, StringComparison.Ordinal))
        {
            error = $"Terminal action '{id}' cannot move from '{previous.Status}' back to '{status}'.";
            return false;
        }

        var selector = previous?.Selector.DeepClone().AsObject() ?? new JsonObject();
        if (automationId.Length > 0)
        {
            selector["automationId"] = automationId;
        }
        action = new SimulatorAgentAppGraphLiveAction(
            id,
            destinationId,
            toolName,
            automationId,
            selector,
            semanticMeaning,
            status,
            previous?.AttemptCount ?? 0,
            NormalizeOptional(ReadString(value, "lastOutcome")) ?? previous?.LastOutcome ?? string.Empty,
            NormalizeOptional(ReadString(value, "resultDestinationId")) ?? previous?.ResultDestinationId,
            previous?.FirstObservedUtc ?? now,
            now);
        return true;
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
                !string.Equals(action.Status, "unsafe", StringComparison.Ordinal));
            actionsExplored = run.Actions.Values.Count(static action =>
                string.Equals(action.Status, "explored", StringComparison.Ordinal)
                || string.Equals(action.Status, "blocked", StringComparison.Ordinal)
                || string.Equals(action.Status, "unavailable", StringComparison.Ordinal));
        }

        var scrollContainersObserved = run.Nodes.Count > 0
            ? run.Nodes.Count
            : legacyCoverage.ScrollContainersObserved;
        var scrollContainersCompleted = run.Nodes.Count > 0
            ? run.Nodes.Values.Count(static node =>
                node.ScrollStatus is "not_scrollable" or "complete" or "blocked")
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
        if (edge.AutomationId.Length == 0)
        {
            return;
        }

        var action = run.Actions.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.DestinationId, edge.From, StringComparison.Ordinal)
            && string.Equals(candidate.AutomationId, edge.AutomationId, StringComparison.Ordinal));
        if (action is null)
        {
            return;
        }

        run.Actions[action.Id] = action with
        {
            Status = "explored",
            LastOutcome = $"Verified transition to '{edge.To}'.",
            ResultDestinationId = edge.To,
            UpdatedUtc = now
        };
    }

    private static SimulatorAgentAppGraphLiveAction? FindMatchingAction(
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions,
        string destinationId,
        string toolName,
        string automationId)
    {
        var normalizedAutomationId = NormalizeOptional(automationId) ?? string.Empty;
        return actions.FirstOrDefault(action =>
            string.Equals(action.DestinationId, destinationId, StringComparison.Ordinal)
            && string.Equals(action.ToolName, toolName, StringComparison.Ordinal)
            && string.Equals(action.AutomationId, normalizedAutomationId, StringComparison.Ordinal));
    }

    private static IReadOnlyList<SimulatorAgentAppGraphLiveAction> BuildPendingActions(
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions)
        => actions
            .Where(static action => action.Status is "queued" or "attempted")
            .OrderBy(static action => action.AttemptCount)
            .ThenBy(static action => action.FirstObservedUtc)
            .ThenBy(static action => action.Id, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> BuildStructureGaps(MutableRun run)
    {
        var gaps = new List<string>();
        var childHostIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var navigationHost in run.NavigationHosts.Values)
        {
            if (!run.Nodes.ContainsKey(navigationHost.DestinationId))
            {
                gaps.Add($"Navigation host '{navigationHost.Id}' is missing destination '{navigationHost.DestinationId}'.");
            }
            if (navigationHost.ChildDestinationIds.Count < 2)
            {
                gaps.Add($"Navigation host '{navigationHost.Id}' has only one child; inventory every flyout, tab-bar, drawer, or rail destination.");
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
                if (childHostIds.TryGetValue(childDestinationId, out var existingHostId)
                    && !string.Equals(existingHostId, navigationHost.Id, StringComparison.Ordinal))
                {
                    gaps.Add($"Destination '{childDestinationId}' belongs to both navigation hosts '{existingHostId}' and '{navigationHost.Id}'.");
                }
                else
                {
                    childHostIds[childDestinationId] = navigationHost.Id;
                }
            }
        }

        var tabMemberships = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tabGroup in run.TabGroups.Values)
        {
            if (!run.Nodes.ContainsKey(tabGroup.ParentDestinationId))
            {
                gaps.Add($"Tab group '{tabGroup.Id}' is missing parent destination '{tabGroup.ParentDestinationId}'.");
            }
            if (tabGroup.TabDestinationIds.Count < 2)
            {
                gaps.Add($"Tab group '{tabGroup.Id}' has only one tab; record the initially selected tab and every visible sibling tab before navigating.");
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
                if (tabMemberships.TryGetValue(tabDestinationId, out var existingGroupId)
                    && !string.Equals(existingGroupId, tabGroup.Id, StringComparison.Ordinal))
                {
                    gaps.Add($"Destination '{tabDestinationId}' belongs to both tab groups '{existingGroupId}' and '{tabGroup.Id}'.");
                }
                else
                {
                    tabMemberships[tabDestinationId] = tabGroup.Id;
                }
            }
        }

        return gaps.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string BuildFrontierGuidance(
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions)
    {
        var pending = BuildPendingActions(actions);
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
            run.Coverage with { Gaps = run.Coverage.Gaps.ToArray() },
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
            run.Trace.ToArray());

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

        if (number.TryGetValue<decimal>(out var decimalValue)) return decimalValue;
        if (number.TryGetValue<double>(out var doubleValue)) return (decimal)doubleValue;
        if (number.TryGetValue<int>(out var intValue)) return intValue;
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

        public long TraceSequence { get; set; }

        public List<SimulatorAgentAppGraphLiveTraceEntry> Trace { get; } = [];
    }
}
