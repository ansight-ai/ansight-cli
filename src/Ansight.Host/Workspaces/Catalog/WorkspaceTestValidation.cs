namespace Ansight.Host.Workspaces.Catalog;

public sealed record WorkspaceTestValidation(
    string Prompt,
    IReadOnlyList<string> Assertions);
