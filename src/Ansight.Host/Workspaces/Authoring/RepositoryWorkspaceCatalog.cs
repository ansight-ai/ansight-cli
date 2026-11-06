using Ansight.Host.Workspaces;
using Ansight.Host.Trends;

namespace Ansight.Host.Workspaces.Authoring;

/// <summary>
/// Read-only repository modules discovered for a linked app workspace.
/// </summary>
public sealed record RepositoryWorkspaceCatalog(
    string RepositoryRootPath,
    string AppId,
    IReadOnlyList<WorkspaceTestDefinition> Tests,
    IReadOnlyList<RepositoryTask> Tasks,
    IReadOnlyList<RepositoryAutomationTrigger> Triggers,
    IReadOnlyList<WorkspaceTrendsDefinition> Trends,
    IReadOnlyList<WorkspaceSanitizerDefinition> Sanitizers,
    IReadOnlyList<string> Warnings);
