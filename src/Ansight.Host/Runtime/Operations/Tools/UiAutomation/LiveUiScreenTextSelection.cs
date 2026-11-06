namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiScreenTextSelection
{
    public static LiveUiScreenTextSelectionResult Select(
        IReadOnlyList<SessionScreenshotTextBlock> source, int imageHeight, int limit)
    {
        var compact = new List<SessionScreenshotTextBlock>();

        foreach (var block in source.OrderByDescending(block => block.Text.Length)
            .ThenByDescending(block => block.Confidence))
        {
            var alreadyRepresented = compact.Any(line =>
            {
                var containsSameText = line.Confidence >= block.Confidence - 10
                    && line.Text.Contains(block.Text, StringComparison.OrdinalIgnoreCase)
                    && Covers(line.Bounds, block.Bounds);

                return containsSameText;
            });

            if (!alreadyRepresented)
            {
                compact.Add(block);
            }
        }

        var ordered = compact.OrderBy(block => block.Bounds.Y).ThenBy(block => block.Bounds.X).ToArray();

        if (ordered.Length <= limit)
        {
            return new LiveUiScreenTextSelectionResult(ordered, ordered.Length, source.Count);
        }

        // A long background list must not consume the entire response before a
        // lower-screen sheet or keyboard is represented. Keep reading order within
        // each band, then interleave bands before applying the response limit.
        const int bandCount = 8;
        var bands = Enumerable.Range(0, bandCount)
            .Select(_ => new Queue<SessionScreenshotTextBlock>()).ToArray();

        foreach (var block in ordered)
        {
            var centerY = block.Bounds.Y + block.Bounds.Height / 2.0;
            var band = Math.Clamp((int)(centerY * bandCount / Math.Max(1, imageHeight)), 0, bandCount - 1);
            bands[band].Enqueue(block);
        }

        var selected = new List<SessionScreenshotTextBlock>();

        while (selected.Count < limit)
        {
            foreach (var band in bands)
            {
                if (band.TryDequeue(out var block))
                {
                    selected.Add(block);
                }

                if (selected.Count == limit)
                {
                    break;
                }
            }
        }

        return new LiveUiScreenTextSelectionResult(
            selected.OrderBy(block => block.Bounds.Y).ThenBy(block => block.Bounds.X).ToArray(),
            ordered.Length, source.Count);
    }

    private static bool Covers(SessionSanitizationRegion line, SessionSanitizationRegion word)
    {
        var width = Math.Max(0, Math.Min(line.X + line.Width, word.X + word.Width) - Math.Max(line.X, word.X));
        var height = Math.Max(0, Math.Min(line.Y + line.Height, word.Y + word.Height) - Math.Max(line.Y, word.Y));
        var area = (double)word.Width * word.Height;

        return area > 0 && width * height / area >= 0.9;
    }
}
