namespace Ansight.Host.Runtime.SessionCaptureStorage;

using Ansight.Host;

internal sealed partial class SessionCaptureStore
{
    private static void WriteMotionEvents(
        SessionPathLayout layout,
        DateTimeOffset savedAtUtc,
        IReadOnlyList<SessionApplicationEvent> applicationEvents)
    {
        var filePath = SessionMotionEvents.GetFilePath(layout.SessionDirectoryPath);
        var motionEvents = applicationEvents.Where(SessionMotionEvents.IsMotion).ToArray();
        if (motionEvents.Length == 0)
        {
            SessionCaptureFileSystem.DeleteIfExists(filePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        SessionCaptureFileSystem.WriteJsonAtomic(filePath, new SessionApplicationEventsBlobDocument
        {
            SavedAtUtc = savedAtUtc,
            Events = motionEvents
        });
    }
}
