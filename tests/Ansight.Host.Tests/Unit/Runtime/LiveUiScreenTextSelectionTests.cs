namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class LiveUiScreenTextSelectionTests
{
    [Fact]
    public void DenseBackgroundTextCannotExcludeLowerScreenSheetFromBoundedScan()
    {
        var blocks = Enumerable.Range(0, 80).Select(index => new SessionScreenshotTextBlock(
            $"Background {index}", 95, new SessionSanitizationRegion(0, index, 100, 1))).ToList();
        blocks.Add(new SessionScreenshotTextBlock("Filters", 95, new SessionSanitizationRegion(100, 600, 90, 30)));
        blocks.Add(new SessionScreenshotTextBlock("Close", 95, new SessionSanitizationRegion(0, 610, 40, 30)));

        var result = LiveUiScreenTextSelection.Select(blocks, 800, 6);

        Assert.Equal(6, result.Blocks.Count);
        Assert.Equal(82, result.TotalCount);
        Assert.Contains(result.Blocks, block => block.Text == "Filters");
        Assert.Contains(result.Blocks, block => block.Text == "Close");
        Assert.Equal(result.Blocks.OrderBy(block => block.Bounds.Y), result.Blocks);
    }

    [Fact]
    public void ScanCompactsContainedWordsButPreservesDistinctControlsAndStrongerText()
    {
        SessionScreenshotTextBlock[] blocks =
        [
            new("Route Filters", 94, new SessionSanitizationRegion(0, 400, 140, 20)),
            new("Filters", 98, new SessionSanitizationRegion(60, 400, 80, 20)),
            new("Filters", 97, new SessionSanitizationRegion(0, 40, 80, 20)),
            new("Noisy OCR line", 40, new SessionSanitizationRegion(0, 500, 140, 20)),
            new("OCR", 98, new SessionSanitizationRegion(60, 500, 30, 20))
        ];

        var result = LiveUiScreenTextSelection.Select(blocks, 800, 60);

        Assert.Equal(5, result.RawCount);
        Assert.Equal(4, result.TotalCount);
        Assert.Contains(result.Blocks, block => block.Text == "Route Filters");
        Assert.Contains(result.Blocks, block => block.Text == "Filters" && block.Bounds.Y == 40);
        Assert.Contains(result.Blocks, block => block.Text == "OCR");
    }
}
