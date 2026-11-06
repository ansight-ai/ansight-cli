using System.Diagnostics;
using System.Globalization;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed class SessionScreenshotOcrLine
{
    private readonly List<SessionScreenshotTextBlock> words = [];
    private double confidenceTotal;
    private int left = int.MaxValue;
    private int top = int.MaxValue;
    private int right;
    private int bottom;

    public void Add(string text, double confidence, int x, int y, int width, int height)
    {
        words.Add(new SessionScreenshotTextBlock(
            text,
            confidence,
            new SessionSanitizationRegion(x, y, width, height)));
        confidenceTotal += confidence;
        left = Math.Min(left, x);
        top = Math.Min(top, y);
        right = Math.Max(right, x + width);
        bottom = Math.Max(bottom, y + height);
    }

    public IReadOnlyList<SessionScreenshotTextBlock> BuildBlocks()
    {
        if (words.Count <= 1)
        {
            return words;
        }

        return
        [
            new SessionScreenshotTextBlock(
                string.Join(' ', words.Select(static word => word.Text)),
                confidenceTotal / words.Count,
                new SessionSanitizationRegion(left, top, right - left, bottom - top)),
            .. words
        ];
    }
}
