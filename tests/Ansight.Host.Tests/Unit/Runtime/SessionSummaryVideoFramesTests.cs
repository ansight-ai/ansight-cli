using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Runtime.Operations.Tools.SessionEvidence;
using Ansight.Host.Tests.TestSupport;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionSummaryVideoFramesTests
{
    private static readonly DateTimeOffset start = DateTimeOffset.Parse("2026-10-25T00:00:00Z");

    [Fact]
    public void SelectsInitialFinalErrorAndNavigationFramesWithinTheModelBudget()
    {
        var snapshot = Snapshot(events:
            [new("navigation", "Opened Map", "navigation", "", start.AddSeconds(10), 0)],
            logs: [new(start.AddSeconds(20), "Load failed") { Priority = LogPriority.Error }]);
        var selected = SessionSummaryVideoFrames.Select(snapshot);
        Assert.InRange(selected.Count, 4, SessionSummaryVideoFrames.MaximumFrames);
        Assert.Equal(0, selected[0].PositionUs);
        Assert.Equal(29_999_999, selected[^1].PositionUs);
        Assert.Contains(selected, item => item.PositionUs == 10_250_000);
        Assert.Contains(selected, item => item.PositionUs == 20_000_000);
        Assert.All(selected, item => Assert.InRange(item.PositionUs, 0, 29_999_999));
    }

    [Fact]
    public void SectionWithOnlyVideoUsesTheOriginalMediaOffsetsAndStaysInsideTheSelection()
    {
        var original = Snapshot();
        var section = LocalSessionSummaryRunner.SelectSection(original, start.AddSeconds(12), start.AddSeconds(18));
        Assert.Same(original.VideoRecordings[0], Assert.Single(section.VideoRecordings));
        var frames = SessionSummaryVideoFrames.Select(section);
        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.InRange(frame.PositionUs, 12_000_000, 17_999_999));
        Assert.Throws<ArgumentException>(() => LocalSessionSummaryRunner.SelectSection(original, start.AddSeconds(40), start.AddSeconds(50)));
    }

    [Fact]
    public void MultipleRecordingsDoNotSelectFramesInTheGap()
    {
        var snapshot = Snapshot(videos: [Video("first", 0, 5), Video("second", 20, 5)]);
        var frames = SessionSummaryVideoFrames.Select(snapshot);
        Assert.Contains(frames, frame => frame.Recording.RecordingId == "first");
        Assert.Contains(frames, frame => frame.Recording.RecordingId == "second");
        Assert.All(frames, frame => Assert.True(frame.CapturedAtUtc < start.AddSeconds(5) || frame.CapturedAtUtc >= start.AddSeconds(20)));
    }

    [Fact]
    public async Task ReadsVideoWithoutAnyScreenshotsAndRemovesTemporaryFrames()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(videos: [Video("video", 0, 1)]);
        fixture.WriteVideo(snapshot);
        string temporaryPath;
        using (var evidence = await SessionSummaryVisualEvidence.CreateAsync(fixture.Runtime, snapshot, null, default))
        {
            Assert.True(evidence.UsesVideo);
            Assert.NotEmpty(evidence.Images);
            Assert.All(evidence.Images, image => Assert.StartsWith("Replay video", image.Source));
            Assert.All(fixture.Extractor.Sources, path => Assert.EndsWith("session-video/video.mp4", path));
            temporaryPath = evidence.Images[0].FilePath;
            Assert.True(File.Exists(temporaryPath));
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(temporaryPath)));
    }

    [Fact]
    public async Task DecoderFailureDoesNotFallBackAndCleansTemporaryFiles()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(videos: [Video("video", 0, 1)]);
        fixture.WriteVideo(snapshot);
        fixture.Extractor.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => SessionSummaryVisualEvidence.CreateAsync(fixture.Runtime, snapshot, null, default));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fixture.Extractor.Output!)));
    }

    [Fact]
    public async Task MissingVideoDoesNotSilentlyProduceAScreenshotSummary()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<FileNotFoundException>(() => SessionSummaryVisualEvidence.CreateAsync(fixture.Runtime, Snapshot(), null, default));
        Assert.Empty(fixture.Extractor.Sources);
    }

    [Fact]
    public void VideoFramesUseTheScopedSummaryImageContract()
    {
        using var environment = new TestEnvironment();
        var path = Path.Combine(environment.RootPath, "decoded.jpg");
        using var bitmap = new SKBitmap(10, 10);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 78);
        File.WriteAllBytes(path, data.ToArray());
        var content = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Session evidence" });
        var frames = Enumerable.Range(0, 5).Select(i => new SessionSummaryImage(path, start.AddSeconds(i), "Replay video test")).ToArray();
        Assert.Equal(5, LocalSessionSummaryRunner.AppendFrameImages(frames, content, null, requireVideo: true));
        Assert.Equal(6, content.Count);
        Assert.All(content.Skip(1), part => Assert.Equal("input_image", part!["type"]!.GetValue<string>()));
        Assert.Contains("Attached frame 5", content[0]!["text"]!.GetValue<string>());
    }

    private static SessionVideoRecording Video(string id, int offset, int seconds)
        => new(id, id, start.AddSeconds(offset), seconds * 1_000_000L, 64, 64, 100, "test");

    private static AppSessionSnapshot Snapshot(IReadOnlyList<SessionVideoRecording>? videos = null,
        IReadOnlyList<SessionApplicationEvent>? events = null, IReadOnlyList<LogEntry>? logs = null) => new()
    {
        SessionId = "summary-video", AppId = "test.app", ClientName = "Test", RemoteAddress = "local",
        ConfigId = null, CreatedUtc = start, LastUpdatedUtc = start.AddSeconds(30), Status = "Closed",
        IsHistorical = true, MetricChannels = [], Metrics = [],
        VideoRecordings = videos ?? [Video("video", 0, 30)], ApplicationEvents = events ?? [], Logs = logs ?? []
    };

    private sealed class Fixture : IDisposable
    {
        private readonly TestEnvironment environment = new();
        public RuntimeCoordinator Runtime { get; }
        public Extractor Extractor { get; } = new();
        public Fixture() => Runtime = new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = environment.RootPath, SecureStorageFilePath = environment.SecureStorageFilePath,
            SecureStorageKeyFilePath = environment.SecureStorageKeyFilePath, SessionVideoFrameExtractor = Extractor
        });
        public void WriteVideo(AppSessionSnapshot snapshot)
        {
            var path = Path.Combine(SessionFileLocator.ResolveSessionDirectoryPath(Runtime.ApplicationPaths, snapshot), snapshot.VideoRecordings[0].EntryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "test-recording.mp4"), path);
        }
        public void Dispose() { Runtime.Dispose(); environment.Dispose(); }
    }

    private sealed class Extractor : ISessionVideoFrameExtractor
    {
        public List<string> Sources { get; } = [];
        public string? Output { get; private set; }
        public bool Fail { get; set; }
        public async Task ExtractAsync(string videoPath, TimeSpan position, string imagePath, CancellationToken token = default)
        {
            Sources.Add(videoPath);
            Output = imagePath;
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3], token);
            if (Fail) throw new IOException("Decoder failed.");
        }
    }
}
