using System.IO.Compression;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Tests.Unit.Runtime;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Sanitization;

public sealed class SessionSanitizationProcessorTests
{
    [Fact]
    public void Sanitize_RedactsPiiAcrossLogsMetadataAndVisualTrees()
    {
        var processor = new SessionSanitizationProcessor(CreateSensitiveRegionPolicy());

        var result = processor.Sanitize(CreateSnapshot());

        Assert.Equal("[REDACTED]", result.Snapshot.ClientName);
        Assert.Equal("Contact [REDACTED] from [REDACTED].", Assert.Single(result.Snapshot.Logs).Message);
        Assert.Equal("[REDACTED]", result.Snapshot.CustomProperties?["password"]?.GetValue<string>());
        var networkRequest = Assert.Single(result.Snapshot.NetworkRequests);
        Assert.Contains("[REDACTED]", networkRequest.Url, StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", networkRequest.ResponseHeaders[0].Value);
        var visualTree = Assert.Single(result.Snapshot.VisualTreeSnapshots);
        Assert.Equal(
            "[REDACTED]",
            visualTree.Payload["root"]?["children"]?[0]?["text"]?.GetValue<string>());
        Assert.True(result.ScreenshotRegionsByFrameId.TryGetValue("frame-1", out var regions));
        var region = Assert.Single(regions);
        Assert.Equal(10, region.X);
        Assert.Equal(20, region.Y);
        Assert.Equal(80, region.Width);
        Assert.Equal(24, region.Height);
        Assert.True(result.Report.Build().RedactedStrings >= 4);
    }

    [Fact]
    public void Sanitize_PreservesStructuralIdsAndIgnoresUnformattedNumbers()
    {
        const string networkRequestId = "459410288342428851";
        var processor = new SessionSanitizationProcessor(CreateSensitiveRegionPolicy());
        var snapshot = CreateSnapshot(
            networkRequestId,
            "Request 123456789012 completed; call +61412345678; card 4111111111111111.");

        var result = processor.Sanitize(snapshot);

        Assert.Equal(networkRequestId, Assert.Single(result.Snapshot.NetworkRequests).Id);
        Assert.Equal(
            "Request 123456789012 completed; call [REDACTED]; card [REDACTED].",
            Assert.Single(result.Snapshot.Logs).Message);
    }

    [Fact]
    public void TryWriteScreenshot_BlacksSensitiveRegionsAndPreservesOtherPixels()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.RootPath, "source.png");
        using (var bitmap = new SKBitmap(40, 40))
        {
            bitmap.Erase(SKColors.White);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var output = File.Create(sourcePath);
            data.SaveTo(output);
        }

        var archivePath = Path.Combine(directory.RootPath, "sanitized.zip");
        var processor = new SessionSanitizationProcessor(CreateSensitiveRegionPolicy());
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Assert.True(processor.TryWriteScreenshot(
                sourcePath,
                new SessionImageFrame
                {
                    FrameId = "frame-1",
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Format = "png",
                    Width = 40,
                    Height = 40,
                    Quality = 100,
                    ByteCount = new FileInfo(sourcePath).Length
                },
                archive,
                "screenshots/frame-1.png",
                [new SessionSanitizationRegion(10, 10, 10, 10)]));
        }

        using var readStream = File.OpenRead(archivePath);
        using var readArchive = new ZipArchive(readStream, ZipArchiveMode.Read);
        using var imageStream = Assert.Single(readArchive.Entries).Open();
        using var sanitized = SKBitmap.Decode(imageStream);
        Assert.Equal(SKColors.Black, sanitized.GetPixel(12, 12));
        Assert.Equal(SKColors.White, sanitized.GetPixel(35, 35));
    }

    [Fact]
    public void PiiSafeDefault_RedactsOcrMatchesButKeepsTheRestOfTheScreenshot()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = CreateWhiteScreenshot(directory.RootPath);
        var archivePath = Path.Combine(directory.RootPath, "ocr.zip");
        var processor = new SessionSanitizationProcessor(
            SessionSanitizationPolicy.PiiSafeDefault,
            ocrScanner: new FakeSessionScreenshotOcrScanner());

        WriteScreenshot(processor, sourcePath, archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        using var image = SKBitmap.Decode(Assert.Single(archive.Entries).Open());
        Assert.Equal(SKColors.Black, image.GetPixel(10, 12));
        Assert.Equal(SKColors.White, image.GetPixel(35, 35));
    }

    [Fact]
    public void PiiSafeDefault_MasksTheWholeFrameWhenOcrAndVisualTreeAreUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = CreateWhiteScreenshot(directory.RootPath);
        var archivePath = Path.Combine(directory.RootPath, "uninspected.zip");
        var processor = new SessionSanitizationProcessor(
            SessionSanitizationPolicy.PiiSafeDefault,
            ocrScanner: new FakeSessionScreenshotOcrScanner(SessionScreenshotOcrResult.Unavailable("OCR unavailable")));

        WriteScreenshot(processor, sourcePath, archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        using var image = SKBitmap.Decode(Assert.Single(archive.Entries).Open());
        Assert.Equal(SKColors.Black, image.GetPixel(35, 35));
    }

    [Fact]
    public void PiiSafeDefault_KeepsAFrameWhenOcrFindsNoSensitiveText()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = CreateWhiteScreenshot(directory.RootPath);
        var archivePath = Path.Combine(directory.RootPath, "inspected.zip");
        var processor = new SessionSanitizationProcessor(
            SessionSanitizationPolicy.PiiSafeDefault,
            ocrScanner: new FakeSessionScreenshotOcrScanner(new SessionScreenshotOcrResult(true, "fake", [], null)));

        WriteScreenshot(processor, sourcePath, archivePath);

        using var archive = ZipFile.OpenRead(archivePath);
        using var entry = Assert.Single(archive.Entries).Open();
        using var contents = new MemoryStream();
        entry.CopyTo(contents);
        Assert.Equal(File.ReadAllBytes(sourcePath), contents.ToArray());
    }

    private static string CreateWhiteScreenshot(string rootPath)
    {
        var sourcePath = Path.Combine(rootPath, "source.png");
        using var bitmap = new SKBitmap(40, 40);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(sourcePath);
        data.SaveTo(output);
        return sourcePath;
    }

    private static void WriteScreenshot(SessionSanitizationProcessor processor, string sourcePath, string archivePath)
    {
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Assert.True(processor.TryWriteScreenshot(sourcePath,
            new SessionImageFrame
            {
                FrameId = "frame-1",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Format = "png",
                Width = 40,
                Height = 40,
                Quality = 100,
                ByteCount = new FileInfo(sourcePath).Length
            }, archive, "screenshots/frame-1.png", []));
    }

    [Fact]
    public void PiiSafeDefault_IncludesSanitizedTextArtifactsAndExcludesBinaryArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var artifactsPath = Path.Combine(directory.RootPath, "artifacts");
        Directory.CreateDirectory(artifactsPath);
        File.WriteAllText(Path.Combine(artifactsPath, "request.txt"), "Contact person@example.com.");
        File.WriteAllBytes(Path.Combine(artifactsPath, "capture.bin"), [1, 2, 3]);
        var archivePath = Path.Combine(directory.RootPath, "artifacts.zip");
        var processor = new SessionSanitizationProcessor(SessionSanitizationPolicy.PiiSafeDefault);

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            processor.WriteArtifacts(artifactsPath, archive, relativePath => $"artifacts/{relativePath}");
        }

        using var readStream = File.OpenRead(archivePath);
        using var readArchive = new ZipArchive(readStream, ZipArchiveMode.Read);
        var entry = Assert.Single(readArchive.Entries);
        Assert.Equal("artifacts/request.txt", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("Contact [REDACTED].", reader.ReadToEnd());
    }

    [Fact]
    public void ScriptedSanitizer_DispatchesKindsAndUsesOcrBounds()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var snapshot = CreateSnapshot();
        var frame = Assert.Single(snapshot.Images);
        var screenshotPath = SessionImageArtifactPath.ResolveCapturedImagePath(
            directory.RootPath,
            snapshot.AppId,
            snapshot.SessionId,
            frame);
        Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
        File.WriteAllBytes(screenshotPath, [0]);
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            """
            export function sanitizeSession(session) {
              return { ...session, sessionId: "changed-by-script" };
            }
            export function sanitizeLog(log, { pii }) {
              return { ...log, message: pii.redact(log.message) };
            }
            export function sanitizeNetworkRequest(request) {
              return {
                ...request,
                id: "changed-by-script",
                url: "https://safe.example.test?token=script-secret",
                requestHeaders: [{ name: "Authorization", value: "script-secret" }]
              };
            }
            export async function sanitizeScreenshot(screenshot, { ocr, image, pii }) {
              const scan = await ocr.scan(screenshot);
              return image.redact(scan.blocks.filter(block => pii.matches(block.text)).map(block => block.bounds));
            }
            """);
        using var processor = new ScriptedSessionSanitizationProcessor(
            modulePath,
            runtime.ExecutablePath,
            directory.RootPath,
            snapshot.AppId,
            snapshot.SessionId,
            new FakeSessionScreenshotOcrScanner());

        var result = processor.Sanitize(snapshot);

        Assert.Equal(snapshot.SessionId, result.Snapshot.SessionId);
        Assert.Equal("Contact [REDACTED] from [REDACTED].", Assert.Single(result.Snapshot.Logs).Message);
        var networkRequest = Assert.Single(result.Snapshot.NetworkRequests);
        Assert.Equal("network-1", networkRequest.Id);
        Assert.Equal("https://safe.example.test/?token=%3Credacted%3E", networkRequest.Url);
        Assert.Equal("<redacted>", Assert.Single(networkRequest.RequestHeaders).Value);
        var region = Assert.Single(result.ScreenshotRegionsByFrameId[frame.FrameId]);
        Assert.Equal(new SessionSanitizationRegion(4, 8, 60, 16), region);
    }

    [Fact]
    public void ScriptedSanitizer_SanitizesLargeLogStreamsEntryByEntry()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-20T01:02:03Z");
        var entries = Enumerable.Range(0, 513)
            .Select(index => new LogEntry(
                capturedAtUtc.AddMilliseconds(index),
                $"person@example.com {new string('x', 8_192)}")
                {
                    StreamId = SessionLogStreamIds.AppleUnifiedLog
                })
            .ToArray();
        var snapshot = CreateSnapshot(
            logStreams:
            [
                new SessionLogStream
                {
                    StreamId = SessionLogStreamIds.AppleUnifiedLog,
                    Kind = SessionLogStreamKinds.AppleUnifiedLog,
                    DisplayName = "Apple Unified Log",
                    Entries = entries,
                    TotalEntryCount = entries.Length
                }
            ]);
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            """
            export function sanitizeDefault(value) {
              if ("entries" in value) throw new Error("log stream entries must be sanitized separately");
              return value;
            }
            export function sanitizeLog(log, { pii }) {
              return { ...log, message: pii.redact(log.message) };
            }
            """);
        var progress = new List<SessionOptimizationProgress>();
        using var processor = new ScriptedSessionSanitizationProcessor(
            modulePath,
            runtime.ExecutablePath,
            directory.RootPath,
            snapshot.AppId,
            snapshot.SessionId,
            report: progress.Add);

        var result = processor.Sanitize(snapshot);

        var stream = Assert.Single(result.Snapshot.LogStreams);
        Assert.Equal(entries.Length, stream.Entries.Count);
        Assert.Equal(entries.Length, stream.TotalEntryCount);
        Assert.All(stream.Entries, entry => Assert.StartsWith("[REDACTED] ", entry.Message, StringComparison.Ordinal));
        Assert.NotEmpty(progress);
        Assert.Contains(progress, item => item.Message.Contains("log stream 'apple-unified-log' entry", StringComparison.Ordinal));
        Assert.Equal(progress[^1].Total, progress[^1].Completed);
    }

    [Fact]
    public void ScriptedSanitizer_FailureIdentifiesLogStreamAndEntry()
    {
        var runtime = JavaScriptRuntimeResolver.Resolve("node");
        if (!runtime.IsAvailable)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-20T01:02:03Z");
        var snapshot = CreateSnapshot(
            logStreams:
            [
                new SessionLogStream
                {
                    StreamId = SessionLogStreamIds.AppleUnifiedLog,
                    Kind = SessionLogStreamKinds.AppleUnifiedLog,
                    DisplayName = "Apple Unified Log",
                    Entries =
                    [
                        new LogEntry(capturedAtUtc, "first") { StreamId = SessionLogStreamIds.AppleUnifiedLog },
                        new LogEntry(capturedAtUtc.AddSeconds(1), "second") { StreamId = SessionLogStreamIds.AppleUnifiedLog }
                    ],
                    TotalEntryCount = 2
                }
            ]);
        var modulePath = Path.Combine(directory.RootPath, "sanitizer.ts");
        File.WriteAllText(
            modulePath,
            """
            export function sanitizeDefault(value) { return value; }
            export function sanitizeLog(log) {
              if (log.streamId === "apple-unified-log") throw new Error("cannot sanitize this entry");
              return log;
            }
            """);
        using var processor = new ScriptedSessionSanitizationProcessor(
            modulePath,
            runtime.ExecutablePath,
            directory.RootPath,
            snapshot.AppId,
            snapshot.SessionId);

        var exception = Assert.Throws<InvalidDataException>(() => processor.Sanitize(snapshot));

        Assert.Contains(
            "log stream 'apple-unified-log' entry 1 of 2",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("sanitizeLog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot sanitize this entry", exception.Message, StringComparison.Ordinal);
    }

    private static AppSessionSnapshot CreateSnapshot(
        string networkRequestId = "network-1",
        string logMessage = "Contact person@example.com from 10.0.0.42.",
        IReadOnlyList<SessionLogStream>? logStreams = null)
    {
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-20T01:02:03Z");
        return new AppSessionSnapshot
        {
            SessionId = "session-1",
            AppId = "com.example.app",
            ClientName = "Matthew's iPhone",
            RemoteAddress = "10.0.0.42",
            CreatedUtc = capturedAtUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = capturedAtUtc.AddMinutes(1),
            IsHistorical = true,
            CustomProperties = new JsonObject
            {
                ["password"] = "not-a-pattern-match"
            },
            Logs =
            [
                new LogEntry(capturedAtUtc, logMessage)
            ],
            LogStreams = logStreams ?? [],
            NetworkRequests =
            [
                new SessionNetworkRequest
                {
                    Id = networkRequestId,
                    Source = "test",
                    StartedAtUtc = capturedAtUtc,
                    CompletedAtUtc = capturedAtUtc.AddMilliseconds(100),
                    DurationMilliseconds = 100,
                    Method = "GET",
                    Url = "https://example.test/customer?email=person@example.com",
                    RequestHeaders =
                    [
                        new SessionNetworkHeader { Name = "Accept", Value = "application/json" }
                    ],
                    ResponseHeaders =
                    [
                        new SessionNetworkHeader { Name = "X-Customer", Value = "person@example.com" }
                    ],
                    StatusCode = 200
                }
            ],
            Images =
            [
                new SessionImageFrame
                {
                    FrameId = "frame-1",
                    CapturedAtUtc = capturedAtUtc,
                    Format = "png",
                    Width = 100,
                    Height = 200,
                    Quality = 100,
                    ByteCount = 1
                }
            ],
            VisualTreeSnapshots =
            [
                new SessionVisualTreeSnapshot
                {
                    SnapshotId = "tree-1",
                    CapturedAtUtc = capturedAtUtc,
                    Source = "sdk",
                    NodeCount = 2,
                    ScreenshotFrameId = "frame-1",
                    Payload = new JsonObject
                    {
                        ["coordinateSpace"] = new JsonObject
                        {
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = 100,
                            ["height"] = 200
                        },
                        ["root"] = new JsonObject
                        {
                            ["bounds"] = new JsonObject
                            {
                                ["x"] = 0,
                                ["y"] = 0,
                                ["width"] = 100,
                                ["height"] = 200
                            },
                            ["children"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["text"] = "person@example.com",
                                    ["bounds"] = new JsonObject
                                    {
                                        ["x"] = 10,
                                        ["y"] = 20,
                                        ["width"] = 80,
                                        ["height"] = 24
                                    }
                                }
                            }
                        }
                    }
                }
            ],
            MetricChannels = [],
            Metrics = []
        };
    }

    private static SessionSanitizationPolicy CreateSensitiveRegionPolicy()
    {
        return new SessionSanitizationPolicy
        {
            Id = "region-test",
            Screenshots = new SessionScreenshotSanitizationPolicy
            {
                Mode = SessionScreenshotSanitizationMode.SensitiveRegions,
                Fallback = SessionScreenshotSanitizationFallback.RedactAll,
                PaddingPixels = 6
            }
        };
    }

    private sealed class FakeSessionScreenshotOcrScanner : ISessionScreenshotOcrScanner
    {
        private readonly SessionScreenshotOcrResult? result;

        public FakeSessionScreenshotOcrScanner(SessionScreenshotOcrResult? result = null)
        {
            this.result = result;
        }

        public SessionScreenshotOcrResult Scan(string imageFilePath)
            => result ?? new(
                true,
                "fake",
                [
                    new SessionScreenshotTextBlock(
                        "person@example.com",
                        100,
                        new SessionSanitizationRegion(4, 8, 60, 16))
                ],
                null);
    }
}
