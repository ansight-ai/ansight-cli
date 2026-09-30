namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationCatalog
{
    public static RepositoryAutomationCatalog Empty { get; } = new([]);

    private readonly IReadOnlyDictionary<RepositoryAutomationTriggerIndexKey, RepositoryAutomationTriggerDefinition[]> triggersByIndexKey;
    private readonly RepositoryAutomationTriggerDefinition[] triggers;
    private readonly RepositoryAutomationTrigger[] publicTriggers;

    public RepositoryAutomationCatalog(IEnumerable<RepositoryAutomationTriggerDefinition> triggers)
    {
        this.triggers = triggers.ToArray();
        triggersByIndexKey = this.triggers
            .Where(static trigger => trigger.Enabled)
            .GroupBy(trigger => new RepositoryAutomationTriggerIndexKey(trigger.EventKind, trigger.AppId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        publicTriggers = this.triggers
            .Select(trigger => trigger.ToPublicDefinition())
            .OrderBy(trigger => trigger.RepositoryRootPath, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.TriggerId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<RepositoryAutomationTrigger> PublicTriggers => publicTriggers;

    public IReadOnlyList<RepositoryAutomationTriggerDefinition> Triggers => triggers;

    public bool HasCandidates(string eventKind, string appId)
        => triggersByIndexKey.ContainsKey(new RepositoryAutomationTriggerIndexKey(eventKind, appId));

    public IReadOnlyList<RepositoryAutomationTriggerDefinition> FindMatches(AutomationEventEnvelope envelope)
    {
        var key = new RepositoryAutomationTriggerIndexKey(envelope.Kind, envelope.AppId);
        if (!triggersByIndexKey.TryGetValue(key, out var candidates))
        {
            return Array.Empty<RepositoryAutomationTriggerDefinition>();
        }

        return candidates
            .Where(trigger => trigger.Conditions.All(condition => condition.Matches(envelope)))
            .ToArray();
    }
}
