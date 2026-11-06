using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure;

namespace Ansight.Host.Sessions;

public readonly record struct SessionArtifactExportResult(
    bool IsSuccess,
    string Message,
    string? FilePath,
    SessionArtifactSnapshot? Snapshot,
    SessionArtifactEntry? Entry)
{
    public static SessionArtifactExportResult Success(
        string filePath,
        SessionArtifactSnapshot snapshot,
        SessionArtifactEntry entry)
        => new(true, "Artifact file exported.", filePath, snapshot, entry);

    public static SessionArtifactExportResult Failure(string message)
        => new(false, message, null, null, null);
}
