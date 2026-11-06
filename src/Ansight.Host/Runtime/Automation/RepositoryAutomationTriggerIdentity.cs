namespace Ansight.Host.Runtime.Automation;

internal readonly record struct RepositoryAutomationTriggerIdentity(
    string RepositoryRootPath,
    string AppId,
    string TriggerId);
