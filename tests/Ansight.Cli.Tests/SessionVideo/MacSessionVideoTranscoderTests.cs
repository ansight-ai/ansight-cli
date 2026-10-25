using Ansight.Host.Sessions.Video;
using SkiaSharp;

namespace Ansight.Cli.Tests.SessionVideo;

public sealed class MacSessionVideoTranscoderTests
{
    [Fact]
    public async Task NativeCompressionBoundsDimensionsAndPreservesFinalIdleTime()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var directory = TestDirectory.Create();
        var source = Path.Combine(directory.Path, "source.mp4");
        var output = Path.Combine(directory.Path, "compressed.mp4");
        var image = Path.Combine(directory.Path, "frame.png");
        using (var bitmap = new SKBitmap(1600, 3200))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(image);
            encoded.SaveTo(file);
        }
        await new MacSessionVideoEncoder().EncodeAsync(new(source, 1600, 3200, 4_000_000,
            [new(null, image, 0, 3_250_000, false)]));
        await new MacSessionVideoTranscoder().TranscodeAsync(source, output, TimeSpan.FromSeconds(3.25));
        var info = SessionVideoMp4TimingInspector.ReadVideoInfo(output);
        Assert.Equal(640, info.Width);
        Assert.Equal(1280, info.Height);
        Assert.InRange(info.Duration.TotalSeconds, 3.249, 3.251);
        var timestamps = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(output);
        Assert.True(timestamps.Select((time, index) => index == 0 || time > timestamps[index - 1]).All(increasing => increasing));
    }
}
