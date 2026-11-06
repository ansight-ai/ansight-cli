namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationRepositoryLoad(
    RepositoryAutomationRepositoryConnectionKey Key,
    RepositoryAutomationTriggerDefinition[] Triggers,
    RepositoryAutomationTrigger[] PublicTriggers,
    IReadOnlyList<string> Warnings)
{
    public bool IsSuccess => Warnings.Count == 0 && Triggers.Length > 0;
}
