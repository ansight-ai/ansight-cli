using System.Security.Cryptography;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.WebSocketSessions;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class ScreenshotInspectionTests
{
    internal static JsonObject SystemUiGeometry(string state = "present") => JsonNode.Parse("""
        {"coordinateSpace":"image-normalized","captureTarget":"display",
         "safeAreaInsets":{"top":0.1,"right":0,"bottom":0,"left":0},
         "systemUi":{
           "statusBar":{"state":"present","regions":[{"x":0,"y":0,"width":1,"height":0.1}]},
           "navigationBar":{"state":"absent","regions":[]},
           "displayCutout":{"state":"unknown","regions":[],"reason":"No exact bounds."}}}
        """)!.AsObject().WithStatusBar(state);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemUiMasksIgnoreClockButStillFindChangedCube(bool changedCube)
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(100, 100, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 0, 100, 10, paint);
            if (changedCube) canvas.DrawRect(50, 50, 10, 10, paint);
        }));
        fixture.State.SetSessionImageGeometry(fixture.SessionId, actual.FrameId, SystemUiGeometry());
        var result = await fixture.Call(false, new JsonObject
        {
            ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId },
            ["ignoreSystemUi"] = new JsonArray("statusBar"),
            // Overlapping manual region must count only once.
            ["ignoreRegions"] = JsonNode.Parse("""[{"x":0,"y":0,"width":0.5,"height":0.1}]"""),
            ["maxDifferenceRatio"] = 0d
        });
        Assert.Equal(!changedCube, result["passed"]!.GetValue<bool>());
        Assert.Equal(1000, result["ignoredPixelCount"]!.GetValue<long>());
        Assert.Equal(changedCube ? 100 : 0, result["differentPixelCount"]!.GetValue<long>());
        Assert.Equal(actual.FrameId, result["systemUiMaskResolutions"]![0]!["frameId"]!.GetValue<string>());
        Assert.NotNull(result["image"]!["geometry"]);
    }

    [Fact]
    public void KnownAbsentDoesNotMaskTopInsetAndUnknownFailsClearly()
    {
        var absent = ScreenshotSystemUiMasks.Resolve(SystemUiGeometry("absent"), ["statusBar"], [], "frame");
        Assert.Empty(absent.Regions);
        Assert.Equal("absent", absent.Resolutions[0]!["state"]!.GetValue<string>());
        Assert.Throws<ScreenshotGeometryUnavailableException>(() => ScreenshotSystemUiMasks.Resolve(null, ["statusBar"], [], "old"));
        Assert.Throws<ScreenshotGeometryUnavailableException>(() => ScreenshotSystemUiMasks.Resolve(SystemUiGeometry(), ["displayCutout"], [], "frame"));
        Assert.Empty(ScreenshotSystemUiMasks.Resolve(null, [], [], "old").Regions);
    }

    [Fact]
    public void InvalidGeometryCannotSupplyMasks()
    {
        var geometry = SystemUiGeometry();
        geometry["systemUi"]!["statusBar"]!["regions"]![0]!["width"] = 1.1;
        Assert.Null(ScreenshotGeometry.Validate(geometry));
        geometry = SystemUiGeometry();
        geometry["safeAreaInsets"]!["top"] = 1;
        Assert.Null(ScreenshotGeometry.Validate(geometry));
        geometry = SystemUiGeometry("absent");
        geometry["systemUi"]!["statusBar"]!["regions"] = JsonNode.Parse("""[{"x":0,"y":0,"width":1,"height":0.1}]""");
        Assert.Null(ScreenshotGeometry.Validate(geometry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamGeometryJoinsEitherOrderAndInvalidatesConflictingDuplicates(bool metadataFirst)
    {
        var updates = new Dictionary<string, JsonObject?>();
        var join = new ScreenshotGeometryJoin((id, geometry) => updates[id] = geometry);
        var frame = new ParsedSessionImageFrame(DateTimeOffset.FromUnixTimeMilliseconds(1000), "jpeg", 100, 100, 60, new byte[] { 1, 2, 3 });
        var metadata = Metadata(frame);
        if (metadataFirst) Assert.True(join.TryAcceptMetadata(metadata.ToJsonString()));
        Assert.Equal(metadataFirst, join.BeforeFrame(frame) is not null);
        join.AfterFrame(frame, "frame");
        if (!metadataFirst) Assert.True(join.TryAcceptMetadata(metadata.ToJsonString()));
        Assert.NotNull(updates["frame"]);
        join.TryAcceptMetadata(metadata.ToJsonString());
        Assert.NotNull(updates["frame"]); // Identical delivery retry.
        metadata["geometry"] = SystemUiGeometry("absent");
        join.TryAcceptMetadata(metadata.ToJsonString());
        Assert.Null(updates["frame"]);
        Assert.Null(join.BeforeFrame(frame));
    }

    [Fact]
    public void StreamJoinDoesNotCrossHashesConnectionsExpiryOrAmbiguousCaptures()
    {
        var updates = new Dictionary<string, JsonObject?>();
        var now = DateTimeOffset.UtcNow;
        var frame = new ParsedSessionImageFrame(now, "jpeg", 100, 100, 60, new byte[] { 1, 2, 3 });
        var join = new ScreenshotGeometryJoin((id, geometry) => updates[id] = geometry, () => now);
        join.TryAcceptMetadata(Metadata(frame).ToJsonString());
        Assert.Null(join.BeforeFrame(frame with { Bytes = new byte[] { 4, 5 } }));
        Assert.Null(new ScreenshotGeometryJoin((_, _) => { }).BeforeFrame(frame));
        join.AfterFrame(frame, "one");
        join.AfterFrame(frame, "two");
        Assert.Null(updates["one"]);
        Assert.Null(updates["two"]);
        var later = frame with { CapturedAtUtc = now.AddSeconds(1) };
        join.TryAcceptMetadata(Metadata(later).ToJsonString());
        now = now.AddSeconds(31);
        Assert.Null(join.BeforeFrame(later));
        for (var i = 0; i < 130; i++) join.TryAcceptMetadata(Metadata(frame with { Width = i + 1 }).ToJsonString());
        Assert.Null(join.BeforeFrame(frame with { Width = 1 }));
    }

    private static JsonObject Metadata(ParsedSessionImageFrame frame) => new()
    {
        ["type"] = "CLIENT_SCREENSHOT_GEOMETRY", ["capturedAtEpochMs"] = frame.CapturedAtUtc.ToUnixTimeMilliseconds(),
        ["width"] = frame.Width, ["height"] = frame.Height,
        ["sha256"] = Convert.ToHexString(SHA256.HashData(frame.Bytes.Span)).ToLowerInvariant(), ["geometry"] = SystemUiGeometry()
    };
}

internal static class SystemUiGeometryTestExtensions
{
    internal static JsonObject WithStatusBar(this JsonObject geometry, string state)
    {
        geometry["systemUi"]!["statusBar"]!["state"] = state;
        if (state != "present") geometry["systemUi"]!["statusBar"]!["regions"] = new JsonArray();
        return geometry;
    }
}
