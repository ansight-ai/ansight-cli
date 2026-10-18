using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class ScreenshotInspectionTests
{
    [Fact]
    public async Task IgnoreRegionsExcludeUnionFromScoresHighlightsAndFindingsAndRetainEvidence()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(100, 100, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 0, 100, 10, paint); // Changing status bar.
            canvas.DrawRect(0, 0, 10, 100, paint); // Overlapping dynamic sidebar.
            canvas.DrawRect(50, 50, 10, 10, paint); // Real regression outside the masks.
        }));
        var masks = JsonNode.Parse("""[{"x":0,"y":0,"width":1,"height":0.1},{"x":0,"y":0,"width":0.1,"height":1}]""")!;
        var result = await fixture.Call(false, new JsonObject
        {
            ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId },
            ["ignoreRegions"] = masks.DeepClone()
        });
        Assert.Equal(1900, result["ignoredPixelCount"]!.GetValue<long>());
        Assert.Equal(8100, result["totalPixelCount"]!.GetValue<long>());
        Assert.Equal(100, result["differentPixelCount"]!.GetValue<long>());
        Assert.Equal(100 / 8100d, result["differenceRatio"]!.GetValue<double>(), 10);
        Assert.Equal(100 * 765 / (8100d * 4), result["meanChannelDifference"]!.GetValue<double>(), 10);
        // Dividing by the full image would incorrectly pass at the default 1% threshold.
        Assert.False(result["passed"]!.GetValue<bool>());
        var region = Assert.Single(result["regions"]!.AsArray())!;
        Assert.Equal(0.5, region["geometry"]!["x"]!.GetValue<double>());
        Assert.Equal(0.5, region["geometry"]!["y"]!.GetValue<double>());
        Assert.Equal(100, region["differentPixelCount"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(masks, result["ignoreRegions"]));
        var evidence = Assert.Single(fixture.Snapshot.ArtifactSnapshots);
        using var overlay = SKBitmap.Decode(fixture.ArtifactPath(evidence, "overlay.png"));
        Assert.Equal(SKColors.Black, overlay.GetPixel(50, 5)); // Masked changes aren't painted.
        Assert.NotEqual(SKColors.Black, overlay.GetPixel(50, 50));
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ArtifactPath(evidence, "result.json")))!;
        Assert.True(JsonNode.DeepEquals(masks, saved["ignoreRegions"]));
        Assert.Equal(1900, saved["ignoredPixelCount"]!.GetValue<long>());
    }

    [Fact]
    public async Task ChangesOnlyInsideMaskPassAndReturnNoChangedRegions()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(100, 100, canvas =>
        { using var paint = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(80, 0, 20, 5, paint); }));
        var args = new JsonObject { ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId }, ["maxDifferenceRatio"] = 0d };
        var unmasked = await fixture.Call(false, args);
        Assert.False(unmasked["passed"]!.GetValue<bool>());
        Assert.Equal(0, unmasked["ignoredPixelCount"]!.GetValue<long>());
        Assert.Empty(unmasked["ignoreRegions"]!.AsArray());
        args["ignoreRegions"] = JsonNode.Parse("""[{"x":0.8,"y":0,"width":0.2,"height":0.05}]""");
        var result = await fixture.Call(false, args);
        Assert.True(result["passed"]!.GetValue<bool>());
        Assert.Equal(100, result["ignoredPixelCount"]!.GetValue<long>());
        Assert.Equal(9900, result["totalPixelCount"]!.GetValue<long>());
        Assert.Equal(0d, result["differenceRatio"]!.GetValue<double>());
        Assert.Equal(0d, result["meanChannelDifference"]!.GetValue<double>());
        Assert.Empty(result["regions"]!.AsArray());
    }

    [Fact]
    public async Task MasksFollowNormalizedActualCoordinatesAfterResizeAndDownscaling()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(200, 200));
        var actual = await fixture.Frame(Png(400, 200, canvas =>
        { using var paint = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(200, 0, 200, 100, paint); }));
        var result = await fixture.Call(false, new JsonObject
        {
            ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId },
            ["resizeToReference"] = true, ["maxWidth"] = 100, ["maxDifferenceRatio"] = 0d,
            ["ignoreRegions"] = JsonNode.Parse("""[{"x":0.5,"y":0,"width":0.5,"height":0.5}]""")
        });
        Assert.True(result["passed"]!.GetValue<bool>());
        Assert.Equal(2500, result["ignoredPixelCount"]!.GetValue<long>());
        Assert.Equal(7500, result["totalPixelCount"]!.GetValue<long>());
        Assert.Empty(result["regions"]!.AsArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"x\":0,\"y\":0,\"width\":0}]")]
    [InlineData("[{\"x\":0,\"y\":0,\"width\":0,\"height\":0.1}]")]
    [InlineData("[{\"x\":-0.1,\"y\":0,\"width\":0.1,\"height\":0.1}]")]
    [InlineData("[{\"x\":0.9,\"y\":0,\"width\":0.2,\"height\":0.1}]")]
    [InlineData("[{\"x\":1,\"y\":0,\"width\":1e-20,\"height\":0.1}]")]
    [InlineData("[{\"x\":0,\"y\":0.9,\"width\":0.2,\"height\":0.2}]")]
    [InlineData("[{\"x\":\"0\",\"y\":0,\"width\":0.1,\"height\":0.1}]")]
    [InlineData("[{\"x\":0,\"y\":0,\"width\":0.1,\"height\":0.1,\"extra\":true}]")]
    public void MasksRejectMalformedAndOutOfBoundsRectangles(string json)
        => Assert.Throws<ArgumentException>(() => ScreenshotIgnoreRegions.Read(JsonNode.Parse(json)));

    [Fact]
    public void MasksRejectNonFiniteCoordinatesAndTooManyRegions()
    {
        var item = new JsonObject { ["x"] = double.NaN, ["y"] = 0, ["width"] = 0.1, ["height"] = 0.1 };
        Assert.Throws<ArgumentException>(() => ScreenshotIgnoreRegions.Read(new JsonArray(item)));
        item["x"] = double.PositiveInfinity;
        Assert.Throws<ArgumentException>(() => ScreenshotIgnoreRegions.Read(new JsonArray(item.DeepClone())));
        item["x"] = 0;
        var regions = new JsonArray(Enumerable.Range(0, 65).Select(_ => item.DeepClone()).ToArray());
        Assert.Throws<ArgumentException>(() => ScreenshotIgnoreRegions.Read(regions));
    }

    [Fact]
    public void MasksRoundOutwardAndHandleSubpixelAndImageEdgeRectangles()
    {
        var rectangles = ScreenshotIgnoreRegions.Read(JsonNode.Parse("""
            [{"x":0.101,"y":0.201,"width":0.009,"height":0.009},
             {"x":0.8,"y":0.99,"width":0.2,"height":0.01},
             {"x":0.3,"y":0.3,"width":0.00000000001,"height":0.00000000001}]
            """));
        var mask = ScreenshotIgnoreRegions.Rasterize(rectangles, 100, 100, CancellationToken.None);
        Assert.Equal(22, mask.IgnoredPixelCount);
        Assert.True(mask.Pixels[20 * 100 + 10]);
        Assert.True(mask.Pixels[30 * 100 + 30]);
        Assert.True(mask.Pixels[99 * 100 + 99]);
        Assert.False(mask.Pixels[21 * 100 + 11]);
    }

    [Fact]
    public async Task FullyMaskedComparisonReturnsAnErrorInsteadOfPassing()
    {
        using var fixture = new Fixture();
        var frame = await fixture.Frame(Png(100, 100));
        var result = await new ScreenshotInspectionTool(fixture.Services, false).ExecuteAsync(new JsonObject
        {
            ["sessionId"] = fixture.SessionId,
            ["image"] = new JsonObject { ["frameId"] = frame.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = frame.FrameId },
            ["ignoreRegions"] = JsonNode.Parse("""[{"x":0,"y":0,"width":0.6,"height":1},{"x":0.5,"y":0,"width":0.5,"height":1}]""")
        }, null);
        Assert.True(result.Payload!["isError"]!.GetValue<bool>());
        Assert.Contains("Leave at least one pixel unmasked", result.Payload.ToJsonString());
        Assert.Empty(fixture.Snapshot.ArtifactSnapshots);
    }

    [Fact]
    public void LegacyAssertionCalculatorUsesSameMaskAndDenominator()
    {
        using var fixture = new Fixture();
        var baseline = Path.Combine(fixture.Environment.RootPath, "baseline.png");
        var current = Path.Combine(fixture.Environment.RootPath, "current.png");
        File.WriteAllBytes(baseline, Png(100, 100));
        File.WriteAllBytes(current, Png(100, 100, canvas => canvas.Clear(SKColors.Black)));
        var masks = ScreenshotIgnoreRegions.Read(JsonNode.Parse("""[{"x":0,"y":0,"width":0.5,"height":1}]"""));
        var result = ScreenshotDifferenceCalculator.Compare(baseline, current, 100, 16, masks);
        Assert.Equal(5000, result.IgnoredPixelCount);
        Assert.Equal(5000, result.TotalPixelCount);
        Assert.Equal(5000, result.DifferentPixelCount);
        Assert.Equal(1, result.DifferenceRatio);
        Assert.Equal(765 / 4d, result.MeanChannelDifference);
        Assert.Throws<ArgumentException>(() => ScreenshotDifferenceCalculator.Compare(baseline, current, 100, 16,
            [new ScreenshotIgnoreRegion(0, 0, 1, 1)]));
    }
}
