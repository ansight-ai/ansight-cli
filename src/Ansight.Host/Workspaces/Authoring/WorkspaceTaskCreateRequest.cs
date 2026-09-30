namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceTaskCreateRequest(
    string WorkspacePath,
    string TaskId,
    string? Title = null,
    string? Description = null,
    string? AppId = null,
    bool Overwrite = false);
