using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionOptimizationTests
{
    [Fact]
    public void VisualTreeTypeSelection_BlankSourceMatchesEveryCaptureSourceForTheType()
    {
        var selection = new SessionVisualTreeTypeSelection(
            "maui",
            "ansight.maui.visual-tree.compact.v2",
            "ios",
            string.Empty);

        Assert.True(selection.Matches(CreateVisualTree("maui.get_visual_tree")));
        Assert.True(selection.Matches(CreateVisualTree("sdk.touchCapture")));
        Assert.False(selection.Matches(CreateVisualTree("sdk.touchCapture", visualTreeKind: "native")));
    }

    [Fact]
    public void Optimize_ConvertsAndPersistsWebP_RemovesDuplicates_AndIsIdempotent()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var png = CreatePng();
        var imported = state.ImportSessionSnapshot(CreateSnapshot(), new Dictionary<string, byte[]>
        {
            ["a"] = png, ["b"] = png
        }).ImportedSession!;
        var progress = new List<SessionOptimizationProgress>();

        var result = state.OptimizeSession(imported.SessionId, progress.Add);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.RemovedScreenshotCount);
        Assert.Equal(1, result.ConvertedImageCount);
        Assert.Contains(progress, item => item.Message == "Checking screenshots for duplicates…" && item.Completed == 2 && item.Total == 2);
        Assert.Contains(progress, item => item.Message == "Converting screenshots to WebP…" && item.Completed == 1 && item.Total == 1);
        Assert.Contains(progress, item => item.Message == "Saving converted screenshots…");
        var reloadedState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        Assert.True(reloadedState.TryGetSessionSnapshot(imported.SessionId, out var reloaded));
        var frame = Assert.Single(reloaded!.Images);
        Assert.Equal("a", frame.FrameId);
        Assert.Equal("webp", frame.Format);
        var path = SessionImageArtifactPath.ResolveCapturedImagePath(store.CapturesRootPath, imported.AppId, imported.SessionId, frame);
        using var decoded = SKBitmap.Decode(path);
        Assert.NotNull(decoded);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(new FileInfo(path).Length, frame.ByteCount);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".png")));
        Assert.Equal(0, reloadedState.OptimizeSession(imported.SessionId).ConvertedImageCount);
    }

    [Fact]
    public void Optimize_InvalidImagePreservesOriginalFilesAndMetadata()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var state = new RuntimeState(store);
        var imported = state.ImportSessionSnapshot(CreateSnapshot(), new Dictionary<string, byte[]>
        {
            ["a"] = CreatePng(), ["b"] = [1, 2, 3]
        }).ImportedSession!;

        Assert.Throws<InvalidDataException>(() => state.OptimizeSession(imported.SessionId));

        Assert.True(state.TryGetSessionSnapshot(imported.SessionId, out var snapshot));
        Assert.All(snapshot!.Images, frame =>
        {
            Assert.Equal("png", frame.Format);
            Assert.True(File.Exists(SessionImageArtifactPath.ResolveCapturedImagePath(store.CapturesRootPath, imported.AppId, imported.SessionId, frame)));
        });
        Assert.Empty(Directory.GetFiles(SessionImageArtifactPath.ResolveCapturedImagesDirectoryPath(store.CapturesRootPath, imported.AppId, imported.SessionId), "*.tmp"));
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SessionVisualTreeSnapshot CreateVisualTree(string source, string visualTreeKind = "maui")
        => new()
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            CapturedAtUtc = DateTimeOffset.Parse("2026-09-23T00:00:00Z"),
            VisualTreeKind = visualTreeKind,
            VisualTreeFormat = "ansight.maui.visual-tree.compact.v2",
            RuntimePlatform = "ios",
            Source = source,
            NodeCount = 1
        };

    private static AppSessionSnapshot CreateSnapshot()
    {
        var created = DateTimeOffset.Parse("2026-09-14T00:00:00Z");
        return new()
        {
            SessionId = "optimize-test", AppId = "com.example.optimize", ClientName = "Optimize", RemoteAddress = "local",
            CreatedUtc = created, LastUpdatedUtc = created.AddSeconds(3), ConfigId = null, Status = "Complete", IsHistorical = true,
            Images = new[] { "a", "b" }.Select((id, index) => new SessionImageFrame
            {
                FrameId = id, CapturedAtUtc = created.AddSeconds(index + 1), Format = "png",
                Width = 64, Height = 64, Quality = 100, ByteCount = 0
            }).ToArray(), MetricChannels = [], Metrics = []
        };
    }
}
