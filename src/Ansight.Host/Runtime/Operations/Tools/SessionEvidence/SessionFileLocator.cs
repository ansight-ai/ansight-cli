using Ansight.Infrastructure;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal static class SessionFileLocator
{
    public static string ResolveSessionDirectoryPath(IApplicationPaths applicationPaths, AppSessionSnapshot snapshot)
    {
        var rootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(applicationPaths);
        return SessionImageArtifactPath.ResolveSessionDirectoryPath(rootPath, snapshot.AppId, snapshot.SessionId);
    }

    public static string ResolveScreenshotPath(IApplicationPaths applicationPaths, AppSessionSnapshot snapshot, SessionImageFrame frame)
    {
        var rootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(applicationPaths);
        return SessionImageArtifactPath.ResolveCapturedImagePath(rootPath, snapshot.AppId, snapshot.SessionId, frame);
    }

    public static string ResolveArtifactsRootPath(IApplicationPaths applicationPaths, AppSessionSnapshot snapshot)
        => Path.Combine(ResolveSessionDirectoryPath(applicationPaths, snapshot), "artifacts");

    public static string ResolveArtifactSnapshotDirectoryPath(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot snapshot,
        SessionArtifactSnapshot artifactSnapshot)
    {
        return Path.Combine(
            ResolveArtifactsRootPath(applicationPaths, snapshot),
            FileNameUtil.Sanitize(artifactSnapshot.ArtifactDirectoryName));
    }

    public static bool TryResolveArtifactEntryPath(
        IApplicationPaths applicationPaths,
        AppSessionSnapshot session,
        SessionArtifactSnapshot artifactSnapshot,
        SessionArtifactEntry entry,
        out string filePath)
    {
        filePath = string.Empty;
        var snapshotDirectoryPath = ResolveArtifactSnapshotDirectoryPath(applicationPaths, session, artifactSnapshot);
        var relativePath = NormalizeArtifactPath(entry.ArchiveRelativePath);
        var candidatePath = string.IsNullOrWhiteSpace(relativePath)
            ? snapshotDirectoryPath
            : Path.Combine([snapshotDirectoryPath, .. relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

        var fullCandidatePath = Path.GetFullPath(candidatePath);
        var fullSnapshotDirectoryPath = Path.GetFullPath(snapshotDirectoryPath);
        if (!fullCandidatePath.StartsWith(fullSnapshotDirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullCandidatePath, fullSnapshotDirectoryPath, StringComparison.Ordinal))
        {
            return false;
        }

        filePath = fullCandidatePath;
        return true;
    }

    public static string NormalizeArtifactPath(string? path)
    {
        return SessionArtifactPathNormalizer.Normalize(path);
    }
}
