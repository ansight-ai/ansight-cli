namespace Ansight.Host.Runtime.State;

using SkiaSharp;

internal sealed partial class RuntimeState
{
    public SessionOptimizationResult OptimizeSession(
        string sessionId,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
        => OptimizeSession(sessionId, new SessionOptimizationOptions(), report, cancellationToken);

    public SessionOptimizationResult OptimizeSession(
        string sessionId,
        SessionOptimizationOptions options,
        Action<SessionOptimizationProgress>? report = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalization = NormalizeSession(sessionId, options, report, cancellationToken);
        if (!normalization.IsSuccess)
            return new(false, normalization.Message, 0, 0, 0);

        sessionId = sessionId.Trim();
        if (!TryGetSessionSnapshot(sessionId, out var snapshot) || snapshot is null)
            throw new InvalidOperationException("The session is no longer available.");

        var converted = new List<ConvertedSessionImage>();
        try
        {
            var frames = options.OptimizeScreenshots
                ? snapshot.Images.Where(frame =>
                    SessionImageArtifactPath.ResolveFileExtension(frame.Format) != "webp").ToArray()
                : [];
            report?.Invoke(new("Converting screenshots to WebP…", 0, frames.Length));
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                    sessionCaptureStore.CapturesRootPath, snapshot.AppId, sessionId, frame);
                using var bitmap = SKBitmap.Decode(sourcePath)
                    ?? throw new InvalidDataException($"Screenshot '{frame.FrameId}' could not be decoded. Original images have been kept.");
                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Webp, 80)
                    ?? throw new InvalidDataException($"Screenshot '{frame.FrameId}' could not be converted to WebP.");
                var replacement = new SessionImageFrame
                {
                    FrameId = frame.FrameId, CapturedAtUtc = frame.CapturedAtUtc,
                    Format = "webp", Width = bitmap.Width, Height = bitmap.Height,
                    Quality = 80, ByteCount = data.Size
                };
                var destinationPath = SessionImageArtifactPath.ResolveCapturedImagePath(
                    sessionCaptureStore.CapturesRootPath, snapshot.AppId, sessionId, replacement);
                var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
                converted.Add(new(replacement, temporaryPath, destinationPath));
                using (var output = File.Create(temporaryPath)) data.SaveTo(output);
                report?.Invoke(new("Converting screenshots to WebP…", converted.Count, frames.Length));
            }

            cancellationToken.ThrowIfCancellationRequested();
            report?.Invoke(new("Saving converted screenshots…"));
            if (converted.Count > 0)
            {
                lock (gate)
                {
                    if (!sessionsById.TryGetValue(sessionId, out var session)
                        || IsSessionLive(session)
                        || !session.Images.Select(ImageVersion).SequenceEqual(snapshot.Images.Select(ImageVersion)))
                        throw new InvalidOperationException("The session changed during optimisation. Please retry.");

                    foreach (var item in converted) File.Move(item.TemporaryPath, item.DestinationPath, overwrite: true);
                    var replacements = converted.ToDictionary(item => item.Frame.FrameId, item => item.Frame);
                    var originalFrames = session.Images.ToArray();
                    for (var index = 0; index < session.Images.Count; index++)
                        if (replacements.TryGetValue(session.Images[index].FrameId, out var replacement))
                            session.Images[index] = replacement;
                    session.MarkAllContentChanged();
                    var optimizedSnapshot = SessionStateMapper.CreateSnapshot(session);
                    try
                    {
                        // Persist the new references before pruning the original image files.
                        sessionCaptureStore.Save(optimizedSnapshot);
                    }
                    catch
                    {
                        session.Images.Clear();
                        session.Images.AddRange(originalFrames);
                        session.MarkAllContentChanged();
                        throw;
                    }
                    sessionCaptureStore.SaveAndPruneDetachedContent(optimizedSnapshot);
                }
                QueueSessionUpdated(sessionId);
            }

            return new(true,
                $"Optimised session: removed {normalization.RemovedScreenshotCount:N0} duplicate screenshots and "
                + $"{normalization.RemovedVisualTreeSnapshotCount:N0} duplicate visual trees; converted {converted.Count:N0} images to WebP.",
                normalization.RemovedScreenshotCount, normalization.RemovedVisualTreeSnapshotCount, converted.Count);
        }
        finally
        {
            foreach (var item in converted)
                if (File.Exists(item.TemporaryPath)) File.Delete(item.TemporaryPath);
        }
    }

    private static string ImageVersion(SessionImageFrame frame)
        => $"{frame.FrameId}:{frame.Format}:{frame.ByteCount}:{frame.CapturedAtUtc:O}";

    private sealed record ConvertedSessionImage(SessionImageFrame Frame, string TemporaryPath, string DestinationPath);
}
