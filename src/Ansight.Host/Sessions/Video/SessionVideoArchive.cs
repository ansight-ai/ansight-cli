using System.IO.Compression;

namespace Ansight.Host;

internal static class SessionVideoArchive
{
    internal static void Write(ZipArchive archive, string capturesRoot, AppSessionSnapshot source,
        IReadOnlyList<SessionVideoRecording> recordings)
    {
        var directory = SessionImageArtifactPath.ResolveSessionDirectoryPath(capturesRoot, source.AppId, source.SessionId);
        foreach (var recording in recordings)
        {
            recording.Validate();
            using var input = File.OpenRead(Path.Combine(directory, recording.EntryPath));
            if (input.Length != recording.ByteSize) throw new InvalidDataException("Session video size mismatch.");
            using var output = archive.CreateEntry(recording.EntryPath, CompressionLevel.NoCompression).Open();
            input.CopyTo(output);
        }
    }
}
