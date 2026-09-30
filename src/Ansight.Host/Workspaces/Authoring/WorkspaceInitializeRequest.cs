namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceInitializeRequest(
    string WorkspacePath,
    bool OverwriteSupportFiles = false);
