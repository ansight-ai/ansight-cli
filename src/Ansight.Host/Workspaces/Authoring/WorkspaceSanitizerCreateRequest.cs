namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceSanitizerCreateRequest(
    string WorkspacePath,
    string SanitizerId,
    bool Overwrite = false);
