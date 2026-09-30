namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceTestCreateRequest(
    string WorkspacePath,
    string TestId,
    string AppId,
    string? Name = null,
    string? Prompt = null,
    string? ValidationPrompt = null,
    IReadOnlyList<string>? Assertions = null,
    IReadOnlyList<string>? RequiredSecrets = null,
    bool Overwrite = false);
