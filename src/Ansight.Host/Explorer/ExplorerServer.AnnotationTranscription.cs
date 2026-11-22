using System.Net;
using Ansight.Host.Transcription;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private async Task<bool> TryHandleAnnotationTranscriptionAsync(HttpListenerRequest request, HttpListenerResponse response,
        string[] segments, bool isHead, CancellationToken cancellationToken)
    {
        if (segments.Length is not (4 or 5) || segments[0] != "api" || segments[1] != "sessions" || segments[3] != "annotation-transcription")
        {
            return false;
        }
        var sessionId = Uri.UnescapeDataString(segments[2]);
        if (!isExplorer && sessionId != InitialSessionId)
        {
            throw new InvalidDataException("Session not found.");
        }
        if (segments.Length == 4 && request.HttpMethod is "GET" or "HEAD")
        {
            await WriteJsonAsync(response, new { isConfigured = AnnotationTranscriber.IsInstalled(runtime.ApplicationPaths.ApplicationDataPath), isSupported = AnnotationTranscriber.IsSupported, model = AnnotationTranscriber.Model }, HttpStatusCode.OK, isHead, cancellationToken);
            return true;
        }
        if (request.HttpMethod != "POST")
        {
            return false;
        }
        if (request.Headers["Origin"] is { } origin && origin != request.Url?.GetLeftPart(UriPartial.Authority))
        {
            throw new InvalidDataException("Transcription must originate from this local UI.");
        }
        if (segments.Length == 5)
        {
            if (segments[4] != "prepare") return false;
            using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setupTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            await AnnotationTranscriber.PrepareAsync(runtime.ApplicationPaths.ApplicationDataPath, setupTimeout.Token);
            await WriteJsonAsync(response, new { isConfigured = true, model = AnnotationTranscriber.Model }, HttpStatusCode.OK, false, cancellationToken);
            return true;
        }
        var contentType = request.ContentType ?? "";
        if (request.ContentLength64 > AnnotationTranscriber.MaximumBytes)
        {
            throw new InvalidDataException("The recording exceeds the two-minute limit.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var audio = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.InputStream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (audio.Length + count > AnnotationTranscriber.MaximumBytes)
            {
                throw new InvalidDataException("The recording exceeds the two-minute limit.");
            }
            audio.Write(buffer, 0, count);
        }
        // Audio exists only in memory and is never added to session artifacts or logs.
        var text = await AnnotationTranscriber.TranscribeAsync(audio.ToArray(), contentType, runtime.ApplicationPaths.ApplicationDataPath, timeout.Token);
        await WriteJsonAsync(response, new { text }, HttpStatusCode.OK, false, cancellationToken);
        return true;
    }
}
