namespace Ansight.Host.Workspaces.Archives;

public sealed record WorkspaceArchiveResult(
    bool IsSuccess,
    string Message,
    string WorkspacePath,
    string ArchivePath,
    int FileCount = 0,
    long ExpandedBytes = 0);
