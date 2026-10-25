namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed partial class SessionCaptureStore
{
    internal void SaveVideoFile(string appId, string sessionId, SessionVideoRecording recording, string sourcePath)
        => RunWithSessionPersistenceLock(sessionId, () =>
        {
            if (IsDeleted(sessionId)) throw new IOException("The session has been deleted.");
            CopyVideoFile(GetSessionLayout(appId, sessionId).SessionDirectoryPath, recording, sourcePath);
        });

    private static void CopyVideoFile(string sessionDirectory, SessionVideoRecording recording, string sourcePath)
    {
        recording.Validate();
        if (new FileInfo(sourcePath).Length != recording.ByteSize)
            throw new InvalidDataException("The recording size does not match its metadata.");
        var destination = Path.Combine(sessionDirectory, recording.EntryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(sourcePath, temporary);
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
                file.Flush(flushToDisk: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void PruneDetachedVideoFiles(SessionPathLayout layout, AppSessionSnapshot snapshot)
    {
        var directory = Path.Combine(layout.SessionDirectoryPath, "session-video");
        if (!Directory.Exists(directory)) return;
        var keep = snapshot.VideoRecordings.Select(video => video.RecordingId + ".mp4").ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.mp4"))
            if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
    }
}
