using System.Threading.Channels;

namespace Ansight.Host.Explorer;

internal sealed partial class ExplorerServer
{
    private sealed record OptimizeSessionRequest(
        bool EncodeVideo = false,
        bool OptimizeScreenshots = true,
        IReadOnlyList<SessionVisualTreeTypeSelection>? VisualTreeTypes = null);

    private async Task OptimizeSessionAsync(
        HttpListenerResponse response,
        string sessionId,
        OptimizeSessionRequest request,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;
        response.Headers["Cache-Control"] = "no-store";
        var events = Channel.CreateBounded<object>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false
        });
        void Report(SessionOptimizationProgress progress)
            => events.Writer.TryWrite(new { status = "running", progress });

        // Run CPU and native encoder work off the response writer so progress is flushed immediately.
        var work = Task.Run(async () =>
        {
            SessionOptimizationResult? result = null;
            try
            {
                if (request.EncodeVideo && runtime.SessionVideoEncoder is null)
                    throw new InvalidOperationException("Video encoding is unavailable on this host. Turn off video export to optimise the local session.");
                result = runtime.SessionEditing.Optimize(
                    sessionId,
                    new SessionOptimizationOptions(request.OptimizeScreenshots, request.VisualTreeTypes),
                    Report,
                    cancellationToken);
                if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
                if (request.EncodeVideo)
                {
                    var archivePath = await ExportSessionToDesktopAsync(sessionId, true, Report, cancellationToken).ConfigureAwait(false);
                    result = result with { ArchiveFilePath = archivePath, Message = result.Message + $" Video ZIP saved to {archivePath}." };
                }
                events.Writer.TryWrite(new { status = "success", result });
            }
            catch (Exception exception)
            {
                events.Writer.TryWrite(new
                {
                    status = "error",
                    message = result?.IsSuccess == true
                        ? result.Message + " Video export failed: " + exception.Message
                        : exception.Message
                });
            }
            finally { events.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, jsonOptions) + "\n");
                await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await work.ConfigureAwait(false);
            response.Close();
        }
    }

    private async Task<string> ExportSessionToDesktopAsync(string sessionId, bool encodeVideo, Action<SessionOptimizationProgress>? report, CancellationToken cancellationToken)
    {
        if (runtime.AppTools.IsConnected(sessionId))
            throw new InvalidOperationException("Finish recording before exporting this session.");
        if (!runtime.Sessions.TryGetSnapshot(sessionId, out var snapshot) || snapshot is null)
            throw new InvalidOperationException("Session not found.");
        if (encodeVideo && snapshot.Images.Count == 0)
            throw new InvalidOperationException("This session has no screenshots to encode.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ansight-desktop-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var fileName = $"{FileNameUtil.Sanitize(snapshot.Name ?? sessionId)}{(encodeVideo ? "-video" : "")}.ansight-session.zip";
        var archivePath = Path.Combine(temporaryDirectory, fileName);
        try
        {
            report?.Invoke(new("Preparing the session ZIP…"));
            var exported = await runtime.SessionArchives.ExportSessionArchiveAsync(sessionId, archivePath, cancellationToken).ConfigureAwait(false);
            if (!exported.IsSuccess) throw new InvalidOperationException(exported.Message);
            if (encodeVideo)
            {
                using var transformed = await SessionArchiveVideoTransformer.TransformAsync(
                    archivePath, snapshot, runtime.SessionVideoEncoder!, cancellationToken, report).ConfigureAwait(false);
                if (!transformed.ContainsVideo) throw new InvalidOperationException("No screenshot files were available for video encoding.");
                File.Copy(transformed.ArchiveFilePath, archivePath, overwrite: true);
            }
            report?.Invoke(new("Saving the ZIP to Desktop…"));
            return await ArtifactDesktopActions.ExportToDesktopAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
