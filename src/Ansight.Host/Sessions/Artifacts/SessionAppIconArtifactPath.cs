namespace Ansight.Host.Utilities;

public static class SessionAppIconArtifactPath
{
    private const string SessionCapturesDirectoryName = "session-captures";
    private const string DefaultIconFileName = "app-icon";

    public static string ResolveSessionIconPath(
        string sessionCapturesRootPath,
        string appId,
        string sessionId,
        SessionAppIcon icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCapturesRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(icon);

        return Path.Combine(
            sessionCapturesRootPath.Trim(),
            FileNameUtil.Sanitize(appId),
            FileNameUtil.Sanitize(sessionId),
            BuildFileName(icon));
    }

    public static string ResolveSessionIconPath(string sessionDirectoryPath, SessionAppIcon icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectoryPath);
        ArgumentNullException.ThrowIfNull(icon);

        return Path.Combine(sessionDirectoryPath.Trim(), BuildFileName(icon));
    }

    public static string ResolveSessionCapturesRootPath(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        return Path.Combine(applicationPaths.ApplicationDataPath, SessionCapturesDirectoryName);
    }

    public static string ResolveArchiveEntryPath(SessionAppIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        return $"session-assets/{BuildFileName(icon)}";
    }

    public static string BuildFileName(SessionAppIcon icon)
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (!string.IsNullOrWhiteSpace(icon.FileName))
        {
            return FileNameUtil.Sanitize(icon.FileName);
        }

        return BuildFileName(icon.Format);
    }

    public static string BuildFileName(string? format)
    {
        return $"{DefaultIconFileName}.{ResolveFileExtension(format)}";
    }

    public static string ResolveFileExtension(string? format)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format)
            ? "png"
            : format.Trim().TrimStart('.').ToLowerInvariant();

        return normalizedFormat is "jpg" or "jpeg"
            ? "jpg"
            : normalizedFormat;
    }
}
