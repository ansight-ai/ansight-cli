namespace Ansight.Host.Workspaces.Catalog;

public sealed record WorkspaceTestCatalogResult(
    string WorkspacePath,
    IReadOnlyList<WorkspaceTestDefinition> Tests,
    IReadOnlyList<string> Warnings);
