using System.Text.Json.Nodes;
using Ansight.Host.Explorer.TaskExtraction;
using Ansight.Host.Files;
using Ansight.Host.Models.Session;
using SkiaSharp;

namespace Ansight.Host.Runtime.Tasks;

internal static class ReplayExtractionEvidence
{
    private const int MaximumSelectorEvidenceNodes = 120;
    private const int MaximumSelectorEvidenceTimes = 12;
    private const int MaximumScreenshotCount = 6;
    private const int MaximumScreenshotWidth = 1024;
    private const int ScreenshotJpegQuality = 78;

    internal static IReadOnlyList<LocalTaskSelectorEvidenceNode> SelectUiEvidence(
        IReadOnlyList<SessionVisualTreeSnapshot> trees)
    {
        var times = LocalTaskSelectorEvidence.Create(trees).Nodes
            .Where(static node => node.AutomationId is not null || node.TextValues.Count > 0)
            .GroupBy(static node => node.CapturedAtUtc)
            .OrderBy(static group => group.Key)
            .Select(static group => group.ToArray())
            .ToArray();
        var selectedTimes = SampleEvenly(times, MaximumSelectorEvidenceTimes).ToArray();

        if (selectedTimes.Length == 0)
        {
            return [];
        }

        return selectedTimes.SelectMany((nodes, index) => SampleEvenly(nodes,
            MaximumSelectorEvidenceNodes / selectedTimes.Length
                + (index < MaximumSelectorEvidenceNodes % selectedTimes.Length ? 1 : 0))).ToArray();
    }

    private static IEnumerable<T> SampleEvenly<T>(IReadOnlyList<T> items, int maximumCount)
    {
        var count = Math.Min(items.Count, maximumCount);

        for (var index = 0; index < count; index++)
        {
            yield return items[count == 1 ? 0 : (int)Math.Round(index * (items.Count - 1d) / (count - 1))];
        }
    }

    internal static async Task<int> AppendScreenshotsAsync(
        RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IEnumerable<DateTimeOffset> sectionEnds,
        JsonArray content,
        CancellationToken cancellationToken)
    {
        var frames = snapshot.Images
            .Where(frame => frame.CapturedAtUtc >= startUtc && frame.CapturedAtUtc <= endUtc)
            .OrderBy(frame => frame.CapturedAtUtc).ToArray();
        var recordings = snapshot.VideoRecordings
            .Where(video => video.StartedUtc <= endUtc && video.EndedUtc > startUtc)
            .OrderBy(video => video.StartedUtc).ToArray();
        var keyTimes = new[] { startUtc, endUtc }.Concat(Enumerable.Range(1, 2).Select(index => startUtc + (endUtc - startUtc) * (index / 3d)))
            .Concat(sectionEnds.Where(time => time >= startUtc && time <= endUtc))
            .Distinct().Take(MaximumScreenshotCount).OrderBy(time => time);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var sampleTimes = new Dictionary<string, IReadOnlyList<long>>(StringComparer.Ordinal);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "ansight-replay-evidence-" + Guid.NewGuid().ToString("N"));
        var appended = 0;

        try
        {
            foreach (var time in keyTimes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string path;
                string description;
                var recording = recordings.LastOrDefault(video => time >= video.StartedUtc && time < video.EndedUtc)
                    ?? recordings.LastOrDefault(video => time == endUtc && time == video.EndedUtc);

                if (recording is not null)
                {
                    var extractor = runtime.SessionVideoFrameExtractor
                        ?? throw new InvalidOperationException("This host cannot decode replay video for test extraction.");
                    var source = Path.Combine(SessionFileLocator.ResolveSessionDirectoryPath(runtime.ApplicationPaths, snapshot), recording.EntryPath);

                    if (!sampleTimes.TryGetValue(source, out var times))
                    {
                        sampleTimes[source] = times = Ansight.Host.Sessions.Video.SessionVideoMp4TimingInspector.ReadPresentationTimesUs(source);
                    }

                    var position = Math.Min((time - recording.StartedUtc).Ticks / 10, recording.DurationUs - 1);
                    var sample = times.LastOrDefault(value => value <= position);

                    if (!selected.Add($"video:{recording.RecordingId}:{sample}"))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(temporaryDirectory);
                    path = Path.Combine(temporaryDirectory, $"frame-{appended}.jpg");
                    await extractor.ExtractAsync(source, TimeSpan.FromTicks(sample * 10), path, cancellationToken).ConfigureAwait(false);
                    description = $"Replay video {recording.RecordingId} at {time:O} (sample {sample / 1_000_000d:F3}s)";
                }
                else
                {
                    var frame = frames.MinBy(frame => Math.Abs((frame.CapturedAtUtc - time).TotalMilliseconds));

                    if (frame is null || !selected.Add("screenshot:" + frame.FrameId))
                    {
                        continue;
                    }

                    path = SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, snapshot, frame);
                    description = $"Screenshot at {frame.CapturedAtUtc:O}";

                    if (!File.Exists(path))
                    {
                        continue;
                    }
                }

                using var original = SKBitmap.Decode(path);

                if (original is null)
                {
                    continue;
                }

                var width = Math.Min(original.Width, MaximumScreenshotWidth);
                var height = Math.Max(1, (int)Math.Round(original.Height * width / (double)original.Width));
                using var resized = width == original.Width ? original.Copy()
                    : original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));

                if (resized is null)
                {
                    continue;
                }

                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, ScreenshotJpegQuality);
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = description + ":" });
                content.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(encoded.ToArray()),
                    ["detail"] = "auto"
                });
                appended++;
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        return appended;
    }
}
