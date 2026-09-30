using System.IO.Compression;
using Ansight.Host;
using SkiaSharp;

namespace Ansight.Cli.Tests.SessionVideo;

public sealed class MacSessionVideoEncoderTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task TransformAsync_UsesCliNativeEncoderAndPreservesFrameTiming(string format)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var archiveFilePath = Path.Combine(directory.Path, "session.zip");
        CreateArchive(archiveFilePath, format);
        var createdUtc = new DateTimeOffset(2026, 8, 31, 1, 2, 3, TimeSpan.Zero);
        var session = new AppSessionSnapshot
        {
            SessionId = "session-1",
            AppId = "com.example.app",
            ClientName = "Example",
            RemoteAddress = "local",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "Disconnected",
            LastUpdatedUtc = createdUtc.AddMilliseconds(2345),
            IsHistorical = true,
            Images =
            [
                CreateFrame("frame-a", createdUtc.AddMilliseconds(123), format),
                CreateFrame("frame-b", createdUtc.AddMilliseconds(789), format)
            ],
            MetricChannels = [],
            Metrics = []
        };
        var encoder = Assert.IsType<MacSessionVideoEncoder>(CliSessionVideoEncoderFactory.Create());

        var progress = new List<Ansight.Host.Sessions.SessionOptimizationProgress>();
        using var result = await SessionArchiveVideoTransformer.TransformAsync(
            archiveFilePath,
            session,
            encoder, report: progress.Add);

        Assert.True(result.ContainsVideo);
        Assert.Contains(progress, item => item.Total == 3 && item.Completed == 3);
        using var archive = ZipFile.OpenRead(result.ArchiveFilePath);
        var videoEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(SessionArchiveVideoTransformer.VideoEntryPath));
        Assert.True(videoEntry.Length > 0);
        Assert.Null(archive.GetEntry($"session-images/frame-a.{format}"));
        Assert.Null(archive.GetEntry($"session-images/frame-b.{format}"));
    }

    private static SessionImageFrame CreateFrame(string frameId, DateTimeOffset capturedAtUtc, string format)
        => new()
        {
            FrameId = frameId,
            CapturedAtUtc = capturedAtUtc,
            Format = format,
            Width = 64,
            Height = 64,
            Quality = 100,
            ByteCount = 0
        };

    private static void CreateArchive(string archiveFilePath, string format)
    {
        using var archive = ZipFile.Open(archiveFilePath, ZipArchiveMode.Create);
        CreateImageEntry(archive, $"session-images/frame-a.{format}", SKColors.CornflowerBlue, format);
        CreateImageEntry(archive, $"session-images/frame-b.{format}", SKColors.OrangeRed, format);
    }

    private static void CreateImageEntry(ZipArchive archive, string entryPath, SKColor color, string format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format == "webp" ? SKEncodedImageFormat.Webp : SKEncodedImageFormat.Png, 100);
        using var entryStream = archive.CreateEntry(entryPath, CompressionLevel.NoCompression).Open();
        data.SaveTo(entryStream);
    }
}
