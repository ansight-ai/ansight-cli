namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task WriteSessionVideoAsync(HttpListenerRequest request, HttpListenerResponse response,
        string sessionId, string recordingId, bool isHead, CancellationToken cancellationToken)
    {
        var snapshot = await LoadReplaySnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var recording = snapshot?.VideoRecordings.FirstOrDefault(video => video.RecordingId == recordingId);
        if (snapshot is null || recording is null)
        {
            await WriteTextAsync(response, "Session video not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }
        recording.Validate();
        var path = Path.Combine(SessionImageArtifactPath.ResolveSessionDirectoryPath(
            SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths), snapshot.AppId, snapshot.SessionId), recording.EntryPath);
        if (!File.Exists(path))
        {
            await WriteTextAsync(response, "Session video file not found.", HttpStatusCode.NotFound, isHead, cancellationToken).ConfigureAwait(false);
            return;
        }
        response.ContentType = "video/mp4";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var range = HttpFileRange.Resolve(GetFileRangeHeader(request, isHead), stream.Length);
        range.Apply(response, stream.Length);
        if (!isHead && range.Length > 0)
        {
            stream.Seek(range.Offset, SeekOrigin.Begin);
            var buffer = new byte[128 * 1024];
            var remaining = range.Length;
            while (remaining > 0)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The recording ended before the requested range.");
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }
        }
        response.Close();
    }
}
