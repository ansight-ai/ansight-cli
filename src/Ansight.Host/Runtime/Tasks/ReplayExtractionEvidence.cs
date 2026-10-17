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
        if (selectedTimes.Length == 0) return [];
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

    internal static int AppendScreenshots(
        RuntimeCoordinator runtime,
        AppSessionSnapshot snapshot,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IEnumerable<DateTimeOffset> sectionEnds,
        JsonArray content)
    {
        var frames = snapshot.Images
            .Where(frame => frame.CapturedAtUtc >= startUtc && frame.CapturedAtUtc <= endUtc)
            .OrderBy(frame => frame.CapturedAtUtc)
            .ToArray();
        if (frames.Length == 0) return 0;
        var appended = 0;
        var keyTimes = new[] { startUtc, endUtc }
            .Concat(Enumerable.Range(1, 2).Select(index => startUtc + (endUtc - startUtc) * (index / 3d)))
            .Concat(sectionEnds)
            .Distinct()
            .Take(MaximumScreenshotCount);
        var selected = keyTimes
            .Select(time => frames.MinBy(frame => Math.Abs((frame.CapturedAtUtc - time).TotalMilliseconds))!)
            .DistinctBy(static frame => frame.FrameId)
            .OrderBy(static frame => frame.CapturedAtUtc);
        foreach (var frame in selected)
        {
            var path = SessionFileLocator.ResolveScreenshotPath(runtime.ApplicationPaths, snapshot, frame);
            if (!File.Exists(path)) continue;
            try
            {
                using var original = SKBitmap.Decode(path);
                if (original is null) continue;
                var width = Math.Min(original.Width, MaximumScreenshotWidth);
                var height = Math.Max(1, (int)Math.Round(original.Height * width / (double)original.Width));
                using var resized = width == original.Width ? original.Copy()
                    : original.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear));
                if (resized is null) continue;
                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, ScreenshotJpegQuality);
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = $"Screenshot at {frame.CapturedAtUtc:O}:" });
                content.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(encoded.ToArray()),
                    ["detail"] = "auto"
                });
                appended++;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                // Missing or unreadable frames should not block test generation.
            }
        }
        return appended;
    }
}
