using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Sessions.Video;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record SessionSummaryImage(string FilePath, DateTimeOffset CapturedAtUtc, string Source);

internal sealed class SessionSummaryVisualEvidence : IDisposable
{
    private string? temporaryDirectory;
    public IReadOnlyList<SessionSummaryImage> Images { get; private set; } = [];
    public bool UsesVideo { get; private set; }

    internal static async Task<SessionSummaryVisualEvidence> CreateAsync(RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot, Action<SessionSummaryProgress>? reportProgress, CancellationToken cancellationToken)
    {
        var result = new SessionSummaryVisualEvidence { UsesVideo = snapshot.VideoRecordings.Count > 0 };
        if (!result.UsesVideo)
        {
            var images = snapshot.Images.OrderBy(item => item.CapturedAtUtc).ToArray();
            var count = Math.Min(images.Length, 5);
            result.Images = Enumerable.Range(0, count).Select(index => images[(int)Math.Round(
                    index * (images.Length - 1) / (double)Math.Max(1, count - 1))])
                .DistinctBy(frame => frame.FrameId)
                .Select(frame => new SessionSummaryImage(SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, snapshot, frame),
                    frame.CapturedAtUtc, "Stored screenshot")).ToArray();
            return result;
        }

        var extractor = runtime.SessionVideoFrameExtractor
            ?? throw new InvalidOperationException("This host cannot decode replay video for summarisation. Update the CLI to enable video frame extraction.");
        var frames = SessionSummaryVideoFrames.Select(snapshot);
        if (frames.Count == 0) throw new InvalidOperationException("No replay video covers the selected summary interval.");
        result.temporaryDirectory = Path.Combine(Path.GetTempPath(), "ansight-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(result.temporaryDirectory);
        try
        {
            var images = new List<SessionSummaryImage>();
            var sampleTimes = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal);
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(new("video", $"Reading video frame {images.Count + 1} of {frames.Count}…", images.Count, frames.Count));
                var source = Path.Combine(SessionFileLocator.ResolveSessionDirectoryPath(runtime.ApplicationPaths, snapshot), frame.Recording.EntryPath);
                if (!File.Exists(source)) throw new FileNotFoundException("The replay video required for summarisation is missing.", source);
                if (!sampleTimes.TryGetValue(source, out var times))
                    sampleTimes[source] = times = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(source);
                // Variable-frame-rate recordings hold the preceding sample until the next one.
                var samplePositionUs = times.LastOrDefault(time => time <= frame.PositionUs);
                var output = Path.Combine(result.temporaryDirectory, $"frame-{images.Count:D2}.jpg");
                await extractor.ExtractAsync(source, TimeSpan.FromTicks(samplePositionUs * 10), output, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                    throw new IOException("The video decoder returned no image for the selected frame.");
                images.Add(new(output, frame.CapturedAtUtc,
                    $"Replay video {frame.Recording.RecordingId}, displayed at {frame.PositionUs / 1_000_000d:F3}s, sample {samplePositionUs / 1_000_000d:F3}s ({frame.Reason})"));
            }
            result.Images = images;
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (temporaryDirectory is null) return;
        try { Directory.Delete(temporaryDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        temporaryDirectory = null;
    }
}
