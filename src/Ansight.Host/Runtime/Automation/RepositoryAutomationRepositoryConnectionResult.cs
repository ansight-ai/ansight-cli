namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationRepositoryConnectionResult(
    bool IsSuccess,
    bool IsConnected,
    string RepositoryRootPath,
    string AppId,
    IReadOnlyList<RepositoryAutomationTrigger> Triggers,
    IReadOnlyList<string> Warnings);
