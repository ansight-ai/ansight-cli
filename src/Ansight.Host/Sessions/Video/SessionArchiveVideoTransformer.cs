using System.IO.Compression;
using System.Text.Json;

namespace Ansight.Host;

public static class SessionArchiveVideoTransformer
{
    public const string VideoEntryPath = "session-video/session.mp4";
    public const string FrameIndexEntryPath = "session-video/frame-index.json";
    public const string FrameIndexSchema = "ansight.session-video-index.v1";
    public const int TimeBaseUnitsPerSecond = 1_000_000;

    private const int MinimumAverageBitRate = 350_000;
    private const int MaximumAverageBitRate = 4_000_000;
    private const double AverageBitsPerPixel = 0.55;
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<SessionArchiveVideoTransformResult> TransformAsync(
        string sourceArchiveFilePath,
        AppSessionSnapshot session,
        ISessionVideoEncoder encoder,
        CancellationToken cancellationToken = default,
        Action<SessionOptimizationProgress>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceArchiveFilePath);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(encoder);
        if (!File.Exists(sourceArchiveFilePath))
        {
            throw new FileNotFoundException("The session archive was not found.", sourceArchiveFilePath);
        }

        if (session.Images.Count == 0)
        {
            return new SessionArchiveVideoTransformResult(sourceArchiveFilePath, containsVideo: false, ownedDirectoryPath: null);
        }

        var workingDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "ansight-session-video",
            Guid.NewGuid().ToString("N"));
        var frameDirectoryPath = Path.Combine(workingDirectoryPath, "frames");
        var videoFilePath = Path.Combine(workingDirectoryPath, "session.mp4");
        var transformedArchiveFilePath = Path.Combine(workingDirectoryPath, "session-video.zip");
        Directory.CreateDirectory(frameDirectoryPath);

        try
        {
            report?.Invoke(new("Preparing images for video encoding…"));
            var extractedFrames = await ExtractFramesAsync(
                sourceArchiveFilePath,
                session.Images,
                frameDirectoryPath,
                cancellationToken).ConfigureAwait(false);
            if (extractedFrames.Count == 0)
            {
                TryDeleteDirectory(workingDirectoryPath);
                return new SessionArchiveVideoTransformResult(sourceArchiveFilePath, containsVideo: false, ownedDirectoryPath: null);
            }

            var dimensions = ResolveVideoDimensions(extractedFrames);
            var timeline = BuildTimeline(session, extractedFrames);
            var encodingFrames = timeline.Frames
                .Select(frame => new SessionVideoEncodingFrame(
                    frame.Source.Frame.FrameId,
                    frame.Source.FilePath,
                    frame.PresentationTimeUs,
                    frame.DurationUs,
                    IsTerminalHold: false))
                .Append(new SessionVideoEncodingFrame(
                    FrameId: null,
                    timeline.Frames[^1].Source.FilePath,
                    timeline.EndPresentationTimeUs,
                    DurationUs: 1,
                    IsTerminalHold: true))
                .ToArray();
            var encodingRequest = new SessionVideoEncodingRequest(
                videoFilePath,
                dimensions.Width,
                dimensions.Height,
                ResolveAverageBitRate(dimensions),
                encodingFrames)
            {
                ReportProgress = (completed, total) => report?.Invoke(new("Encoding video…", completed, total))
            };
            report?.Invoke(new("Encoding video…", 0, encodingFrames.Length));
            var encodingResult = await encoder.EncodeAsync(encodingRequest, cancellationToken).ConfigureAwait(false);

            ValidateEncodingResult(encodingRequest, encodingResult);
            report?.Invoke(new("Packaging the video and frame index…"));
            await CreateTransformedArchiveAsync(
                sourceArchiveFilePath,
                transformedArchiveFilePath,
                videoFilePath,
                dimensions,
                timeline,
                encodingResult,
                extractedFrames,
                cancellationToken).ConfigureAwait(false);

            TryDeleteDirectory(frameDirectoryPath);
            TryDeleteFile(videoFilePath);
            return new SessionArchiveVideoTransformResult(
                transformedArchiveFilePath,
                containsVideo: true,
                workingDirectoryPath);
        }
        catch
        {
            TryDeleteDirectory(workingDirectoryPath);
            throw;
        }
    }

    private static async Task<IReadOnlyList<ExtractedFrame>> ExtractFramesAsync(
        string sourceArchiveFilePath,
        IReadOnlyList<SessionImageFrame> frames,
        string frameDirectoryPath,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(
            sourceArchiveFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var imageEntries = archive.Entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Where(static entry => NormalizeEntryPath(entry.FullName).StartsWith("session-images/", StringComparison.Ordinal))
            .ToArray();
        var extractedFrames = new List<ExtractedFrame>(frames.Count);
        var orderedFrames = frames
            .OrderBy(static frame => frame.CapturedAtUtc)
            .ThenBy(static frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();

        for (var frameIndex = 0; frameIndex < orderedFrames.Length; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = orderedFrames[frameIndex];
            var entryPrefix = $"session-images/{frame.FrameId}.";
            var entry = imageEntries.FirstOrDefault(candidate =>
                NormalizeEntryPath(candidate.FullName).StartsWith(entryPrefix, StringComparison.Ordinal));
            if (entry is null)
            {
                continue;
            }

            var extension = Path.GetExtension(entry.Name);
            var extractedFilePath = Path.Combine(frameDirectoryPath, $"{frameIndex:D8}{extension}");
            await using (var input = entry.Open())
            await using (var output = new FileStream(
                             extractedFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            // The native macOS encoder reads ImageIO formats. Decode WebP explicitly
            // so this path also works on systems without an ImageIO WebP decoder.
            if (string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                using var bitmap = SkiaSharp.SKBitmap.Decode(extractedFilePath)
                    ?? throw new InvalidDataException($"Screenshot '{frame.FrameId}' could not be decoded.");
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                var pngPath = Path.ChangeExtension(extractedFilePath, ".png");
                using (var output = File.Create(pngPath)) png.SaveTo(output);
                File.Delete(extractedFilePath);
                extractedFilePath = pngPath;
            }
            extractedFrames.Add(new ExtractedFrame(frame, extractedFilePath, entry.FullName));
        }

        return extractedFrames;
    }

    private static VideoTimeline BuildTimeline(
        AppSessionSnapshot session,
        IReadOnlyList<ExtractedFrame> extractedFrames)
    {
        var firstCapturedAtUtc = extractedFrames[0].Frame.CapturedAtUtc;
        var timelineFrames = new List<TimelineFrame>(extractedFrames.Count);
        long previousPresentationTimeUs = -1;
        foreach (var extractedFrame in extractedFrames)
        {
            var presentationTimeUs = Math.Max(
                previousPresentationTimeUs + 1,
                ToMicroseconds(extractedFrame.Frame.CapturedAtUtc - firstCapturedAtUtc));
            timelineFrames.Add(new TimelineFrame(extractedFrame, presentationTimeUs, DurationUs: 0));
            previousPresentationTimeUs = presentationTimeUs;
        }

        var requestedEndPresentationTimeUs = ToMicroseconds(session.LastUpdatedUtc - firstCapturedAtUtc);
        var endPresentationTimeUs = Math.Max(previousPresentationTimeUs + 1, requestedEndPresentationTimeUs);
        for (var frameIndex = 0; frameIndex < timelineFrames.Count; frameIndex++)
        {
            var nextPresentationTimeUs = frameIndex + 1 < timelineFrames.Count
                ? timelineFrames[frameIndex + 1].PresentationTimeUs
                : endPresentationTimeUs;
            timelineFrames[frameIndex] = timelineFrames[frameIndex] with
            {
                DurationUs = Math.Max(1, nextPresentationTimeUs - timelineFrames[frameIndex].PresentationTimeUs)
            };
        }

        return new VideoTimeline(
            ToMicroseconds(firstCapturedAtUtc - session.CreatedUtc),
            endPresentationTimeUs,
            timelineFrames);
    }

    private static VideoDimensions ResolveVideoDimensions(IReadOnlyList<ExtractedFrame> extractedFrames)
    {
        var dimensions = extractedFrames
            .Where(static extracted => extracted.Frame.Width > 0 && extracted.Frame.Height > 0)
            .GroupBy(static extracted => new VideoDimensions(extracted.Frame.Width, extracted.Frame.Height))
            .OrderByDescending(static group => group.Count())
            .ThenByDescending(static group => (long)group.Key.Width * group.Key.Height)
            .Select(static group => group.Key)
            .FirstOrDefault();
        if (dimensions.Width <= 0 || dimensions.Height <= 0)
        {
            throw new InvalidDataException("The session screenshots do not contain valid dimensions.");
        }

        return new VideoDimensions(MakeEven(dimensions.Width), MakeEven(dimensions.Height));
    }

    private static int ResolveAverageBitRate(VideoDimensions dimensions)
        => (int)Math.Clamp(
            Math.Round(dimensions.Width * (double)dimensions.Height * AverageBitsPerPixel),
            MinimumAverageBitRate,
            MaximumAverageBitRate);

    private static void ValidateEncodingResult(
        SessionVideoEncodingRequest request,
        SessionVideoEncodingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!File.Exists(request.OutputFilePath) || new FileInfo(request.OutputFilePath).Length == 0)
        {
            throw new InvalidDataException($"The {result.EncoderName} encoder did not create a video file.");
        }

        var expectedPresentationTimesUs = request.Frames
            .Select(static frame => frame.PresentationTimeUs)
            .ToArray();
        if (!expectedPresentationTimesUs.SequenceEqual(result.WrittenPresentationTimesUs))
        {
            throw new InvalidDataException(
                $"The {result.EncoderName} encoder did not preserve the requested frame presentation timestamps.");
        }

        var encodedPresentationTimesUs = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(request.OutputFilePath);
        if (!expectedPresentationTimesUs.SequenceEqual(encodedPresentationTimesUs))
        {
            throw new InvalidDataException(
                $"The {result.EncoderName} MP4 muxer changed the requested frame presentation timestamps.");
        }
    }

    private static async Task CreateTransformedArchiveAsync(
        string sourceArchiveFilePath,
        string transformedArchiveFilePath,
        string videoFilePath,
        VideoDimensions dimensions,
        VideoTimeline timeline,
        SessionVideoEncodingResult encodingResult,
        IReadOnlyList<ExtractedFrame> extractedFrames,
        CancellationToken cancellationToken)
    {
        File.Copy(sourceArchiveFilePath, transformedArchiveFilePath, overwrite: false);
        await using var archiveStream = new FileStream(
            transformedArchiveFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Update, leaveOpen: false);
        var consumedEntryPaths = extractedFrames
            .Select(static frame => NormalizeEntryPath(frame.ArchiveEntryPath))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in archive.Entries
                     .Where(entry => consumedEntryPaths.Contains(NormalizeEntryPath(entry.FullName)))
                     .ToArray())
        {
            entry.Delete();
        }

        archive.GetEntry(VideoEntryPath)?.Delete();
        archive.GetEntry(FrameIndexEntryPath)?.Delete();
        archive.CreateEntryFromFile(videoFilePath, VideoEntryPath, CompressionLevel.NoCompression);

        var index = new SessionVideoFrameIndexDocument
        {
            Schema = FrameIndexSchema,
            Container = "mp4",
            Codec = "h264",
            Encoder = encodingResult.EncoderName,
            TimeBaseUnitsPerSecond = TimeBaseUnitsPerSecond,
            VideoStartOffsetUs = timeline.VideoStartOffsetUs,
            DurationUs = timeline.EndPresentationTimeUs,
            Width = dimensions.Width,
            Height = dimensions.Height,
            Frames = timeline.Frames.Select(frame => new SessionVideoFrameIndexEntry
            {
                FrameId = frame.Source.Frame.FrameId,
                CapturedAtUtc = frame.Source.Frame.CapturedAtUtc,
                PresentationTimeUs = frame.PresentationTimeUs,
                DurationUs = frame.DurationUs,
                SourceWidth = frame.Source.Frame.Width,
                SourceHeight = frame.Source.Frame.Height
            }).ToArray()
        };
        var indexEntry = archive.CreateEntry(FrameIndexEntryPath, CompressionLevel.Optimal);
        await using var indexStream = indexEntry.Open();
        await JsonSerializer.SerializeAsync(indexStream, index, jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static int MakeEven(int value)
        => value % 2 == 0 ? value : checked(value + 1);

    private static long ToMicroseconds(TimeSpan value)
        => value.Ticks / (TimeSpan.TicksPerSecond / TimeBaseUnitsPerSecond);

    private static string NormalizeEntryPath(string value)
        => value.Trim().Replace('\\', '/').TrimStart('/');

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Temporary encoder files are best-effort cleanup only.
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Temporary encoder files are best-effort cleanup only.
        }
    }

    private sealed record ExtractedFrame(
        SessionImageFrame Frame,
        string FilePath,
        string ArchiveEntryPath);

    private sealed record TimelineFrame(
        ExtractedFrame Source,
        long PresentationTimeUs,
        long DurationUs);

    private sealed record VideoTimeline(
        long VideoStartOffsetUs,
        long EndPresentationTimeUs,
        IReadOnlyList<TimelineFrame> Frames);

    private readonly record struct VideoDimensions(int Width, int Height);

    private sealed class SessionVideoFrameIndexDocument
    {
        public string Schema { get; init; } = string.Empty;
        public string Container { get; init; } = string.Empty;
        public string Codec { get; init; } = string.Empty;
        public string Encoder { get; init; } = string.Empty;
        public int TimeBaseUnitsPerSecond { get; init; }
        public long VideoStartOffsetUs { get; init; }
        public long DurationUs { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public IReadOnlyList<SessionVideoFrameIndexEntry> Frames { get; init; } = [];
    }

    private sealed class SessionVideoFrameIndexEntry
    {
        public string FrameId { get; init; } = string.Empty;
        public DateTimeOffset CapturedAtUtc { get; init; }
        public long PresentationTimeUs { get; init; }
        public long DurationUs { get; init; }
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
    }
}
