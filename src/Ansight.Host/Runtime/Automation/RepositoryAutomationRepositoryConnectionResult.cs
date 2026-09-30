namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationRepositoryConnectionResult(
    bool IsSuccess,
    bool IsConnected,
    string RepositoryRootPath,
    string AppId,
    IReadOnlyList<RepositoryAutomationTrigger> Triggers,
    IReadOnlyList<string> Warnings);

internal readonly record struct RepositoryAutomationRepositoryConnectionKey(
    string RepositoryRootPath,
    string AppId);

internal sealed record RepositoryAutomationRepositoryLoad(
    RepositoryAutomationRepositoryConnectionKey Key,
    RepositoryAutomationTriggerDefinition[] Triggers,
    RepositoryAutomationTrigger[] PublicTriggers,
    IReadOnlyList<string> Warnings)
{
    public bool IsSuccess => Warnings.Count == 0 && Triggers.Length > 0;
}

internal readonly record struct RepositoryAutomationTriggerIdentity(
    string RepositoryRootPath,
    string AppId,
    string TriggerId);
