namespace Ansight.Host.SimulatorAgent;

internal static class AppGraphFrontier
{
    public static IReadOnlyList<SimulatorAgentAppGraphLiveAction> Pending(
        IEnumerable<SimulatorAgentAppGraphLiveAction> actions,
        IEnumerable<SimulatorAgentAppGraphLiveNode> nodes,
        string? currentDestinationId)
    {
        var candidates = actions.ToArray();
        var blockedScrollDestinations = nodes
            .Where(node => node.ScrollStatus == "blocked")
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        var destinationAttempts = candidates
            .GroupBy(action => action.DestinationId)
            .ToDictionary(group => group.Key, group => group.Sum(action => (long)action.AttemptCount));

        return candidates
            .Where(action => !action.IsNavigationEssential && action.Status is "queued" or "attempted" or "needs_verification" or "deferred")
            .OrderBy(action => action.Status == "deferred")
            .ThenBy(action => action.ToolName == "ansight_scroll_ui" && blockedScrollDestinations.Contains(action.DestinationId))
            .ThenBy(action => action.AttemptCount > 0)
            .ThenBy(action => action.DestinationId != currentDestinationId)
            .ThenBy(action => destinationAttempts[action.DestinationId])
            .ThenBy(action => action.AttemptCount)
            .ThenBy(action => !action.HasStableAutomationId)
            .ThenBy(action => action.FirstObservedUtc)
            .ThenBy(action => action.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
