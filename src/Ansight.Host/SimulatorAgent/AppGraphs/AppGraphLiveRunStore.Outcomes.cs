using System.Text.Json.Nodes;
using Ansight.Host.AppGraphs;

namespace Ansight.Host.SimulatorAgent.AppGraphs;

internal sealed partial class AppGraphLiveRunStore
{
    public SimulatorAgentAppGraphPendingOutcome? RecordDeliveredInput(
        string runId, string destinationId, string toolName, JsonObject selector,
        string inputId, string evidenceId, string message)
    {
        lock (gate)
        {
            if (!runs.TryGetValue(runId, out var run) || run.CompletedUtc.HasValue)
            {
                return null;
            }

            RecordActionAttempt(runId, destinationId, toolName, selector, true, message, persist: false);

            var normalized = AppGraphActionIdentity.NormalizeSelector(selector);
            var action = FindMatchingAction(run.Actions.Values, destinationId, toolName,
                ReadString(normalized, "automationId") ?? string.Empty, normalized)
                ?? run.Actions[BuildActionId(destinationId, toolName, normalized)];

            if (!action.IsNavigationEssential && action.Status != "explored")
            {
                run.Actions[action.Id] = action with
                {
                    Status = "needs_verification"
                };
            }

            run.PendingOutcome = new SimulatorAgentAppGraphPendingOutcome(
                inputId, action.Id, destinationId, toolName, evidenceId)
            {
                SemanticMeaning = action.SemanticMeaning,
                Selector = action.Selector.DeepClone().AsObject()
            };
            run.CurrentDestinationId = null;
            run.Coverage = DeriveCoverage(run, run.Coverage);
            database.Save(CreateSnapshot(run));

            return run.PendingOutcome;
        }
    }

    private static string? ResolvePendingOutcome(MutableRun run, JsonObject report, DateTimeOffset now)
    {
        if (run.PendingOutcome is not { } pending)
        {
            return report["actionOutcome"] is JsonObject
                ? "There is no delivered input awaiting an actionOutcome. Use null and report retained action updates separately."
                : null;
        }

        if (report["actionOutcome"] is not JsonObject outcome)
        {
            return $"Input '{pending.InputId}' still needs an actionOutcome for action '{pending.ActionId}'. "
                + $"A screen update alone does not resolve it. Inspect evidence '{pending.EvidenceId}' before further input.";
        }

        var matchesPending = ReadString(outcome, "inputId") == pending.InputId
            && (NormalizeOptional(ReadString(outcome, "actionId")) is null || ReadString(outcome, "actionId") == pending.ActionId)
            && ReadStringArray(outcome, "evidenceRefs").Contains(pending.EvidenceId, StringComparer.Ordinal);

        if (!matchesPending)
        {
            return $"actionOutcome must identify input '{pending.InputId}', action '{pending.ActionId}', "
                + $"and retained evidence '{pending.EvidenceId}'. Other actions cannot discharge this obligation.";
        }

        var evidence = NormalizeOptional(ReadString(outcome, "evidence"));
        var reason = NormalizeOptional(ReadString(outcome, "reason"));
        var status = ReadString(outcome, "status");
        var missingCurrentDestination = run.CurrentDestinationId is null
            || !run.Nodes.ContainsKey(run.CurrentDestinationId);
        var missingRequiredDestination = status != "needs_verification" && missingCurrentDestination;
        var missingObservation = evidence is null || missingRequiredDestination;

        if (missingObservation)
        {
            return "actionOutcome requires after-state evidence, not just input delivery. Closing an obligation also requires the observed current destination; needs_verification may leave it unknown while input stays locked.";
        }

        if (status is not ("verified" or "no_change" or "needs_verification" or "deferred" or "blocked"))
        {
            return "actionOutcome.status must be verified, no_change, needs_verification, deferred, or blocked.";
        }

        var needsReason = status is "needs_verification" or "deferred" or "blocked";

        if (needsReason && reason is null)
        {
            return "Unresolved action outcomes require a concrete reason and the remaining work.";
        }

        if (status == "blocked" && ReadString(outcome, "blockerKind") is not ("observation" or "target" or "capability" or "application"))
        {
            return "A blocker must be an observed observation, target, capability, or application limitation. Budget or scope limits are deferred work.";
        }

        var action = run.Actions[pending.ActionId];
        var navigationKind = AppGraphActionIdentity.NavigationKind(action.ToolName, action.AutomationId,
            action.Selector, NormalizeOptional(action.NavigationKind) ?? ReadString(outcome, "navigationKind"));
        action = action with
        {
            NavigationKind = navigationKind
        };

        var resolved = status is "verified" or "no_change";
        run.Actions[action.Id] = action with
        {
            Status = action.IsNavigationEssential && status != "blocked" ? "navigation"
                : resolved ? "explored" : status,
            LastOutcome = $"{status}: {evidence}" + (reason is null ? string.Empty : $" Remaining: {reason}"),
            ResultDestinationId = resolved ? run.CurrentDestinationId : action.ResultDestinationId,
            UpdatedUtc = now
        };

        if (resolved && pending.FromDestinationId != run.CurrentDestinationId)
        {
            // The action and after-state are now tied to one actual input. Retain the
            // observed transition even when the report omitted a separate edge entry.
            var transition = new JsonObject
            {
                ["id"] = $"edge_{pending.ActionId}_{run.CurrentDestinationId}",
                ["from"] = pending.FromDestinationId,
                ["to"] = run.CurrentDestinationId,
                ["automationId"] = action.AutomationId,
                ["selector"] = action.Selector.DeepClone(),
                ["navigationKind"] = action.NavigationKind,
                ["semanticMeaning"] = evidence,
                ["confidence"] = 1m
            };

            if (!TryReadTransition(transition, run.Edges, run.Nodes, run.Actions.Values, now, out var edge, out var error))
            {
                return error;
            }

            run.Edges[edge!.Id] = edge;
        }

        if (pending.ToolName == "ansight_scroll_ui" && status == "deferred")
        {
            var node = run.Nodes[run.CurrentDestinationId!];
            run.Nodes[node.Id] = node with
            {
                ScrollStatus = "in_progress",
                UpdatedUtc = now
            };
        }

        if (status != "needs_verification")
        {
            run.PendingOutcome = null;
        }

        return null;
    }
}
