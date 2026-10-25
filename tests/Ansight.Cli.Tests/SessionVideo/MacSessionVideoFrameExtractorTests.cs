using SkiaSharp;

namespace Ansight.Cli.Tests.SessionVideo;

public sealed class MacSessionVideoFrameExtractorTests
{
    [Fact]
    public async Task NativeDecoderReadsTheRequestedVideoState()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var directory = TestDirectory.Create();
        var video = Path.Combine(directory.Path, "video.mp4");
        var red = WriteImage("red", SKColors.Red);
        var blue = WriteImage("blue", SKColors.Blue);
        await new MacSessionVideoEncoder().EncodeAsync(new(video, 320, 640, 1_200_000,
            [new(null, red, 0, 1_000_000, false), new(null, blue, 1_000_000, 1_000_000, false)]));
        var extractor = new MacSessionVideoFrameExtractor();
        var first = Path.Combine(directory.Path, "first.jpg");
        var last = Path.Combine(directory.Path, "last.jpg");
        await extractor.ExtractAsync(video, TimeSpan.Zero, first);
        await extractor.ExtractAsync(video, TimeSpan.FromSeconds(1), last);
        using var firstImage = SKBitmap.Decode(first);
        using var lastImage = SKBitmap.Decode(last);
        Assert.True(firstImage.GetPixel(100, 100).Red > 220);
        Assert.True(firstImage.GetPixel(100, 100).Blue < 30);
        Assert.True(lastImage.GetPixel(100, 100).Blue > 220);
        Assert.True(lastImage.GetPixel(100, 100).Red < 30);

        string WriteImage(string name, SKColor color)
        {
            var path = Path.Combine(directory.Path, name + ".png");
            using var bitmap = new SKBitmap(320, 640);
            bitmap.Erase(color);
            using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            image.SaveTo(stream);
            return path;
        }
    }
}
