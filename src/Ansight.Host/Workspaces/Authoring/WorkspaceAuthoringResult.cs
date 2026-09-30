namespace Ansight.Host.Workspaces.Authoring;

public sealed record WorkspaceAuthoringResult(
    bool IsSuccess,
    string Message,
    string WorkspacePath,
    string? DefinitionPath,
    IReadOnlyList<string> CreatedFiles,
    IReadOnlyList<string> UpdatedFiles,
    IReadOnlyList<string> ExistingFiles)
{
    public static WorkspaceAuthoringResult Success(
        string message,
        string workspacePath,
        string? definitionPath,
        IReadOnlyList<string> createdFiles,
        IReadOnlyList<string> updatedFiles,
        IReadOnlyList<string> existingFiles)
        => new(
            true,
            message,
            workspacePath,
            definitionPath,
            createdFiles,
            updatedFiles,
            existingFiles);

    public static WorkspaceAuthoringResult Failure(
        string message,
        string workspacePath,
        string? definitionPath = null,
        IReadOnlyList<string>? createdFiles = null,
        IReadOnlyList<string>? updatedFiles = null,
        IReadOnlyList<string>? existingFiles = null)
        => new(
            false,
            message,
            workspacePath,
            definitionPath,
            createdFiles ?? [],
            updatedFiles ?? [],
            existingFiles ?? []);
}
