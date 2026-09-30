namespace Ansight.Host.Runtime.Automation;

/// <summary>
/// Inspection and connection state for repository triggers scoped to one Ansight app ID.
/// </summary>
public sealed record RepositoryAutomationConnection(
    bool IsAvailable,
    bool IsConnected,
    string RepositoryRootPath,
    string AppId,
    IReadOnlyList<RepositoryAutomationTrigger> Triggers,
    IReadOnlyList<string> Warnings);
