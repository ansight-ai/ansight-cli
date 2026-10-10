namespace Ansight.Host.Utilities;

public static class SessionImageArtifactPath
{
    private const string SessionCapturesDirectoryName = "session-captures";
    private const string ImagesDirectoryName = "images";

    public static string ResolveSessionCapturesRootPath(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Combine(applicationPaths.ApplicationDataPath, SessionCapturesDirectoryName);
    }

    public static string ResolveCapturedImagesDirectoryPath(string sessionCapturesRootPath, string appId, string sessionId)
    {
        return Path.Combine(
            ResolveSessionDirectoryPath(sessionCapturesRootPath, appId, sessionId),
            ImagesDirectoryName);
    }

    public static string ResolveSessionDirectoryPath(string sessionCapturesRootPath, string appId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCapturesRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Path.Combine(
            sessionCapturesRootPath.Trim(),
            FileNameUtil.Sanitize(appId),
            FileNameUtil.Sanitize(sessionId));
    }

    public static string ResolveCapturedImagePath(string sessionCapturesRootPath, string appId, string sessionId, SessionImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var imagesDirectoryPath = ResolveCapturedImagesDirectoryPath(sessionCapturesRootPath, appId, sessionId);
        return ResolveCapturedImagePath(imagesDirectoryPath, frame);
    }

    public static string ResolveCapturedImagePath(string capturedImagesDirectoryPath, SessionImageFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturedImagesDirectoryPath);
        ArgumentNullException.ThrowIfNull(frame);

        return Path.Combine(capturedImagesDirectoryPath.Trim(), BuildFileName(frame));
    }

    public static string ResolveArchiveEntryPath(SessionImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return $"session-images/{BuildFileName(frame)}";
    }

    public static string BuildFileName(SessionImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return $"{frame.FrameId}.{ResolveFileExtension(frame.Format)}";
    }

    public static string ResolveFileExtension(string? format)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format)
            ? "bin"
            : format.Trim().TrimStart('.').ToLowerInvariant();

        return normalizedFormat is "jpg" or "jpeg"
            ? "jpg"
            : normalizedFormat;
    }
}
