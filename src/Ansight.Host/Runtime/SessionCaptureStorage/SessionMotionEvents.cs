namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal static class SessionMotionEvents
{
    public const string DirectoryName = "motion";
    public const string FileName = "events.json";

    public static bool IsMotion(SessionApplicationEvent appEvent)
        => string.Equals(appEvent.EventType, "Motion", StringComparison.OrdinalIgnoreCase);

    public static string GetFilePath(string sessionDirectoryPath)
        => Path.Combine(sessionDirectoryPath, DirectoryName, FileName);
}
