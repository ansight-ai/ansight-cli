using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure;

namespace Ansight.Host.Sessions;

public sealed class SessionEvidenceService
{
    private readonly ISessionReader sessionReader;
    private readonly IApplicationPaths applicationPaths;

    internal SessionEvidenceService(
        ISessionReader sessionReader,
        IApplicationPaths applicationPaths)
    {
        this.sessionReader = sessionReader ?? throw new ArgumentNullException(nameof(sessionReader));
        this.applicationPaths = applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths));
    }

    public async Task<SessionScreenshotExportResult> ExportSessionScreenshotAsync(
        string sessionId,
        string? frameId,
        DateTimeOffset? timestampUtc,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var snapshot = await sessionReader.LoadSessionSnapshotAsync(sessionId.Trim(), null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return SessionScreenshotExportResult.Failure($"Session '{sessionId}' was not found.");
        }

        var normalizedFrameId = string.IsNullOrWhiteSpace(frameId) ? null : frameId.Trim();
        var frame = normalizedFrameId is not null
            ? snapshot.Images.FirstOrDefault(candidate => string.Equals(
                candidate.FrameId,
                normalizedFrameId,
                StringComparison.Ordinal))
            : timestampUtc.HasValue
                ? snapshot.Images
                    .OrderBy(candidate => (candidate.CapturedAtUtc - timestampUtc.Value.ToUniversalTime()).Duration())
                    .ThenBy(static candidate => candidate.CapturedAtUtc)
                    .FirstOrDefault()
                : snapshot.Images
                    .OrderByDescending(static candidate => candidate.CapturedAtUtc)
                    .ThenBy(static candidate => candidate.FrameId, StringComparer.Ordinal)
                    .FirstOrDefault();
        if (frame is null)
        {
            return SessionScreenshotExportResult.Failure(
                $"Session '{snapshot.SessionId}' has no matching screenshot frame.");
        }

        var sourcePath = SessionFileLocator.ResolveScreenshotPath(applicationPaths, snapshot, frame);
        if (!File.Exists(sourcePath))
        {
            return SessionScreenshotExportResult.Failure(
                $"Screenshot frame '{frame.FrameId}' metadata exists, but the image file was not found.");
        }

        try
        {
            var destinationPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)
                ?? throw new IOException($"Unable to resolve the parent directory for '{destinationPath}'."));
            await CopyFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            return SessionScreenshotExportResult.Success(destinationPath, frame);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SessionScreenshotExportResult.Failure(exception.Message);
        }
    }

    public async Task<SessionArtifactExportResult> ExportSessionArtifactFileAsync(
        string sessionId,
        string? snapshotId,
        string artifactPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var snapshot = await sessionReader.LoadSessionSnapshotAsync(sessionId.Trim(), null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return SessionArtifactExportResult.Failure($"Session '{sessionId}' was not found.");
        }

        var requestedPath = SessionFileLocator.NormalizeArtifactPath(artifactPath);
        var normalizedSnapshotId = string.IsNullOrWhiteSpace(snapshotId) ? null : snapshotId.Trim();
        foreach (var artifactSnapshot in snapshot.ArtifactSnapshots.Where(candidate =>
                     normalizedSnapshotId is null
                     || string.Equals(candidate.SnapshotId, normalizedSnapshotId, StringComparison.Ordinal)))
        {
            var entry = artifactSnapshot.Entries.FirstOrDefault(candidate =>
                string.Equals(
                    SessionFileLocator.NormalizeArtifactPath(candidate.SnapshotRelativePath),
                    requestedPath,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    SessionFileLocator.NormalizeArtifactPath(candidate.ArchiveRelativePath),
                    requestedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            if (string.Equals(entry.Kind, "directory", StringComparison.OrdinalIgnoreCase))
            {
                return SessionArtifactExportResult.Failure(
                    $"Artifact path '{requestedPath}' is a directory.");
            }

            if (!SessionFileLocator.TryResolveArtifactEntryPath(
                    applicationPaths,
                    snapshot,
                    artifactSnapshot,
                    entry,
                    out var sourcePath)
                || !File.Exists(sourcePath))
            {
                return SessionArtifactExportResult.Failure(
                    $"Artifact file '{requestedPath}' metadata exists, but the local file was not found.");
            }

            try
            {
                var destinationPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath)
                    ?? throw new IOException($"Unable to resolve the parent directory for '{destinationPath}'."));
                await CopyFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
                return SessionArtifactExportResult.Success(
                    destinationPath,
                    artifactSnapshot,
                    entry);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return SessionArtifactExportResult.Failure(exception.Message);
            }
        }

        return SessionArtifactExportResult.Failure($"Artifact file '{requestedPath}' was not found.");
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}

public readonly record struct SessionScreenshotExportResult(
    bool IsSuccess,
    string Message,
    string? FilePath,
    SessionImageFrame? Frame)
{
    public static SessionScreenshotExportResult Success(string filePath, SessionImageFrame frame)
        => new(true, "Screenshot exported.", filePath, frame);

    public static SessionScreenshotExportResult Failure(string message)
        => new(false, message, null, null);
}

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
