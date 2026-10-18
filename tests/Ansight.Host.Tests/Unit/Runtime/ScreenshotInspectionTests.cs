using System.IO.Compression;
using Ansight.Host.Runtime.Operations.Tools.Shared;
using Ansight.Host.Runtime.Operations.Tools.UiAutomation;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.Tests.TestSupport;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class ScreenshotInspectionTests
{
    [Fact]
    public async Task ComparisonRetainsReferenceOverlayCropsAndAnnotationGeometry()
    {
        using var fixture = new Fixture();
        var baseline = Png(100, 100);
        var actual = Png(100, 100, canvas => { using var paint = new SKPaint { Color = SKColors.Purple };
            canvas.DrawRect(10, 20, 10, 10, paint); canvas.DrawRect(70, 80, 5, 5, paint); });
        var frame = await fixture.Frame(actual);
        fixture.Reference(baseline);
        using var scope = ScreenshotWorkspaceScope.Push(fixture.Environment.RootPath);
        var result = await fixture.Call(false, new JsonObject
        {
            ["image"] = new JsonObject { ["frameId"] = frame.FrameId },
            ["reference"] = new JsonObject { ["workspacePath"] = "references/expected.png" },
            ["maxDifferenceRatio"] = 0.01
        });
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal(125, result["differentPixelCount"]!.GetValue<long>());
        Assert.Equal(0.0125, result["differenceRatio"]!.GetValue<double>(), 6);
        Assert.Equal(2, result["regions"]!.AsArray().Count);
        var geometry = result["regions"]![0]!["geometry"]!;
        Assert.Equal(frame.FrameId, geometry["frameId"]!.GetValue<string>());
        Assert.Equal(0.1, geometry["x"]!.GetValue<double>(), 6);
        Assert.Equal(0.2, geometry["y"]!.GetValue<double>(), 6);
        Assert.Equal(0.1, geometry["width"]!.GetValue<double>(), 6);
        var annotation = await new CreateAnnotationTool(fixture.Services).ExecuteAsync(new JsonObject
        {
            ["sessionId"] = fixture.SessionId, ["label"] = "Changed cube",
            ["geometries"] = new JsonArray(geometry.DeepClone())
        }, null);
        Assert.False(annotation.Payload!["isError"]!.GetValue<bool>(), annotation.Payload.ToJsonString());
        fixture.Reference(Png(100, 100, canvas => canvas.Clear(SKColors.Black)));
        var stored = fixture.Snapshot.ArtifactSnapshots.Single();
        Assert.Equal(6, stored.FileCount);
        var expectedPath = fixture.ArtifactPath(stored, "reference.png");
        Assert.Equal(baseline, await File.ReadAllBytesAsync(expectedPath));
        using var crop = SKBitmap.Decode(fixture.ArtifactPath(stored, "region-1.png"));
        Assert.Equal(10, crop.Width); Assert.Equal(10, crop.Height);
        Assert.Equal(SKColors.Purple, crop.GetPixel(0, 0));
        var archivePath = Path.Combine(fixture.Environment.RootPath, "evidence.zip");
        SessionArchiveExporter.Export(fixture.Environment.ApplicationPaths, fixture.Snapshot, archivePath, new SessionBundleExportOptions());
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/reference.png", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/overlay.png", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("/result.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DimensionMismatchRequiresExplicitResizeAndRegionsMapToActual()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(200, 100, canvas =>
        { using var paint = new SKPaint { Color = SKColors.Black }; canvas.DrawRect(100, 20, 40, 30, paint); }));
        var args = new JsonObject { ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId } };
        var mismatch = await fixture.Call(false, args);
        Assert.Equal("dimension-mismatch", mismatch["status"]!.GetValue<string>());
        Assert.False(mismatch["passed"]!.GetValue<bool>());
        Assert.Null(mismatch["differenceRatio"]);
        args["resizeToReference"] = true;
        var resized = await fixture.Call(false, args);
        Assert.Equal("compared", resized["status"]!.GetValue<string>());
        var geometry = resized["regions"]![0]!["geometry"]!;
        Assert.Equal(0.5, geometry["x"]!.GetValue<double>(), 6);
        Assert.Equal(0.2, geometry["width"]!.GetValue<double>(), 6);
        Assert.Equal(actual.FrameId, geometry["frameId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThresholdIsStrictAndAllowedRatioIsInclusive()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(100, 100, canvas =>
        {
            using var paint = new SKPaint { Color = new SKColor(239, 255, 255) };
            canvas.DrawRect(0, 0, 10, 10, paint);
            paint.Color = new SKColor(238, 255, 255); canvas.DrawRect(50, 50, 10, 10, paint);
        }));
        var result = await fixture.Call(false, new JsonObject { ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId } });
        Assert.True(result["passed"]!.GetValue<bool>());
        Assert.Equal(100, result["differentPixelCount"]!.GetValue<long>());
        Assert.Single(result["regions"]!.AsArray());
    }

    [Fact]
    public async Task RegionLimitDoesNotDiscardChangedPixelsFromScore()
    {
        using var fixture = new Fixture();
        var reference = await fixture.Frame(Png(100, 100));
        var actual = await fixture.Frame(Png(100, 100, canvas =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            for (var y = 0; y < 100; y += 10) for (var x = 0; x < 100; x += 10) canvas.DrawRect(x, y, 1, 1, paint);
        }));
        var result = await fixture.Call(false, new JsonObject { ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["frameId"] = reference.FrameId } });
        Assert.Equal(100, result["differentPixelCount"]!.GetValue<long>());
        Assert.Equal(100, result["regionCount"]!.GetValue<int>());
        Assert.True(result["regionsTruncated"]!.GetValue<bool>());
        Assert.Equal(64, result["regions"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("/tmp/outside.png")]
    [InlineData("references/../../outside.png")]
    [InlineData("references\\outside.png")]
    [InlineData("https://example.com/image.png")]
    public void WorkspacePathsRejectEscapes(string path)
        => Assert.Throws<ArgumentException>(() => ScreenshotInspectionEvidence.ResolveWorkspaceImage("/tmp/workspace", path));

    [Fact]
    public void WorkspacePathsRejectSymlinkEscapes()
    {
        using var fixture = new Fixture();
        fixture.Reference(Png(20, 20));
        var link = Path.Combine(fixture.Environment.RootPath, "ansight", "references", "link.png");
        File.CreateSymbolicLink(link, Path.Combine(fixture.Environment.RootPath, "outside.png"));
        Assert.Throws<ArgumentException>(() => ScreenshotInspectionEvidence.ResolveWorkspaceImage(fixture.Environment.RootPath, "references/link.png"));
    }

    [Fact]
    public async Task SemanticAssessmentUsesExpectedAndActualAndHostChecksCounts()
    {
        using var fixture = new Fixture();
        var actual = await fixture.Frame(Png(100, 100));
        var referenceBytes = Png(50, 50);
        fixture.Reference(referenceBytes);
        var evaluator = new FakeEvaluator { Text = ValidAssessment };
        fixture.Services.ScreenshotEvaluator = evaluator;
        using var scope = ScreenshotWorkspaceScope.Push(fixture.Environment.RootPath);
        var result = await fixture.Call(true, new JsonObject
        {
            ["image"] = new JsonObject { ["frameId"] = actual.FrameId },
            ["reference"] = new JsonObject { ["workspacePath"] = "references/expected.png" },
            ["criteria"] = Criteria(2)
        });
        Assert.True(result["passed"]!.GetValue<bool>());
        Assert.Equal(2, result["criteria"]![0]!["observedCount"]!.GetValue<int>());
        Assert.Equal(referenceBytes, evaluator.Request!.Reference!.Bytes);
        Assert.Equal(actual.FrameId, evaluator.Request.Image.FrameId);
        Assert.All(result["regions"]!.AsArray(), region => Assert.Equal(actual.FrameId, region!["geometry"]!["frameId"]!.GetValue<string>()));
        var input = ScreenshotEvaluationRunner.BuildInput(evaluator.Request).ToJsonString();
        Assert.Contains("REFERENCE (expected)", input);
        Assert.Contains("ACTUAL", input);
        Assert.Equal(2, ScreenshotEvaluationRunner.BuildInput(evaluator.Request)[0]!["content"]!.AsArray()
            .Count(item => item!["type"]!.GetValue<string>() == "input_image"));
        var failedCount = ScreenshotAssessment.Parse(Response(ValidAssessment), evaluator.Request.Image, Criteria(3));
        Assert.False(failedCount["passed"]!.GetValue<bool>());
        Assert.Equal("fail", failedCount["verdict"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"criteria\":[],\"objects\":[]}")]
    [InlineData("bad-bounds")]
    [InlineData("unknown-region")]
    [InlineData("duplicate-object")]
    public void InvalidModelEvidenceNeverPasses(string response)
    {
        if (response == "bad-bounds") response = ValidAssessment.Replace("0.1", "2.0");
        if (response == "unknown-region") response = ValidAssessment.Replace("[\"one\",\"two\"]", "[\"missing\"]");
        if (response == "duplicate-object") response = ValidAssessment.Replace("\"id\":\"two\"", "\"id\":\"one\"");
        var result = ScreenshotAssessment.Parse(Response(response), Image(), Criteria(2));
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal("inconclusive", result["verdict"]!.GetValue<string>());
        Assert.Empty(result["regions"]!.AsArray());
    }

    [Fact]
    public void MatchingCountDoesNotPromoteInconclusiveVisionEvidence()
    {
        var result = ScreenshotAssessment.Parse(Response(ValidAssessment.Replace("\"pass\"", "\"inconclusive\"")), Image(), Criteria(2));
        Assert.False(result["passed"]!.GetValue<bool>());
        Assert.Equal("inconclusive", result["verdict"]!.GetValue<string>());
    }

    [Fact]
    public async Task TinyValidRegionsProduceAtLeastOnePixelAndImageEdgeRegionsAreAccepted()
    {
        using var fixture = new Fixture();
        var frame = await fixture.Frame(Png(100, 100));
        var response = ValidAssessment.Replace("\"x\":0.1", "\"x\":0.8")
            .Replace("\"width\":0.2", "\"width\":0.00000000001");
        fixture.Services.ScreenshotEvaluator = new FakeEvaluator { Text = response };
        var result = await fixture.Call(true, new JsonObject { ["image"] = new JsonObject { ["frameId"] = frame.FrameId }, ["criteria"] = Criteria(2) });
        Assert.True(result["passed"]!.GetValue<bool>());
        using var crop = SKBitmap.Decode(fixture.ArtifactPath(fixture.Snapshot.ArtifactSnapshots.Single(), "region-one.png"));
        Assert.Equal(1, crop.Width);
        Assert.True(ScreenshotAssessment.Parse(Response(ValidAssessment.Replace("\"x\":0.1", "\"x\":0.8")), Image(), Criteria(2))["passed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task EvaluationWithoutReferenceSendsOnlyActualAndPreservesCancellation()
    {
        using var fixture = new Fixture();
        var frame = await fixture.Frame(Png(50, 50));
        var evaluator = new FakeEvaluator { Text = ValidAssessment };
        fixture.Services.ScreenshotEvaluator = evaluator;
        var args = new JsonObject { ["image"] = new JsonObject { ["frameId"] = frame.FrameId }, ["criteria"] = Criteria(2) };
        var result = await fixture.Call(true, args);
        Assert.Null(result["reference"]);
        Assert.Null(evaluator.Request!.Reference);
        Assert.Single(ScreenshotEvaluationRunner.BuildInput(evaluator.Request)[0]!["content"]!.AsArray(),
            item => item!["type"]!.GetValue<string>() == "input_image");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var token = ToolExecutionCancellation.Push(cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Call(true, args));
    }

    private const string ValidAssessment = """
        {"criteria":[{"id":"purple","verdict":"pass","explanation":"Two purple cubes are visible.","regionIds":["one","two"]}],
        "objects":[{"id":"one","label":"cube","attributes":{"color":"purple"},"bounds":{"x":0.1,"y":0.1,"width":0.2,"height":0.2}},
        {"id":"two","label":"cube","attributes":{"color":"purple"},"bounds":{"x":0.5,"y":0.1,"width":0.2,"height":0.2}}]}
        """;
    private static JsonArray Criteria(int count) => new(new JsonObject { ["id"] = "purple", ["description"] = "Two cubes must be purple.",
        ["count"] = new JsonObject { ["label"] = "cube", ["attributes"] = new JsonObject { ["color"] = "purple" }, ["equals"] = count } });
    private static ScreenshotEvaluationResponse Response(string text) => new(text, "test-model", "fast", "test-config", "test-response", SimulatorAgentTokenUsage.Empty);
    private static InspectionImage Image() => new(Png(100, 100), 100, 100, "test-hash", "actual-frame", null, DateTimeOffset.UtcNow);
    private static byte[] Png(int width, int height, Action<SKCanvas>? draw = null)
    {
        using var bitmap = new SKBitmap(width, height); using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White); draw?.Invoke(canvas);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }

    private sealed class FakeEvaluator : IScreenshotEvaluator
    {
        public string Text { get; init; } = "";
        public ScreenshotEvaluationRequest? Request { get; private set; }
        public Task<ScreenshotEvaluationResponse> EvaluateAsync(ScreenshotEvaluationRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Request = request; return Task.FromResult(Response(Text)); }
    }

    private sealed class Fixture : IDisposable
    {
        public TestEnvironment Environment { get; } = new();
        public RuntimeState State { get; }
        public OperationServices Services { get; }
        public string SessionId { get; }
        public Fixture()
        {
            State = new RuntimeState(new SessionCaptureStore(Environment.ApplicationPaths));
            SessionId = State.CreateSession("image-app", "Images", IPAddress.Loopback, null, null);
            var composition = new MefHostComposition(Environment.ApplicationPaths, new FileBackedEncryptedStorage(Environment.SecureStorageFilePath));
            var bridge = composition.Get<IAppToolBridge>();
            Services = new OperationServices(State, Environment.ApplicationPaths, composition.Get<IKnownAppStore>(),
                composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(), bridge, null, new SessionResolver(State, bridge));
        }
        public AppSessionSnapshot Snapshot { get { Assert.True(State.TryGetSessionSnapshot(SessionId, out var snapshot)); return snapshot!; } }
        public async Task<SessionImageFrame> Frame(byte[] bytes)
        {
            using var bitmap = SKBitmap.Decode(bytes);
            return (await State.AddSessionEvidenceImageAsync(SessionId, DateTimeOffset.UtcNow, "png", bitmap.Width, bitmap.Height, 100, bytes))!;
        }
        public void Reference(byte[] bytes)
        {
            var directory = Path.Combine(Environment.RootPath, "ansight", "references"); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "expected.png"), bytes);
        }
        public async Task<JsonObject> Call(bool evaluate, JsonObject args)
        {
            args["sessionId"] = SessionId;
            var result = await new ScreenshotInspectionTool(Services, evaluate).ExecuteAsync(args, null);
            Assert.False(result.Payload!["isError"]!.GetValue<bool>(), result.Payload.ToJsonString());
            return result.Payload["structuredContent"]!.AsObject();
        }
        public string ArtifactPath(SessionArtifactSnapshot snapshot, string name)
        {
            Assert.True(SessionFileLocator.TryResolveArtifactEntryPath(Environment.ApplicationPaths, Snapshot, snapshot,
                snapshot.Entries.Single(entry => entry.Name == name), out var path)); return path!;
        }
        public void Dispose() => Environment.Dispose();
    }
}
