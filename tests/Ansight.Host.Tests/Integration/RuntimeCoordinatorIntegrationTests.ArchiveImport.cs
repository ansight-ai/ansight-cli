using System.Buffers.Binary;
using System.IO.Compression;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using SharpZipEntry = ICSharpCode.SharpZipLib.Zip.ZipEntry;
using SharpZipOutputStream = ICSharpCode.SharpZipLib.Zip.ZipOutputStream;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_RestoresExportedSessionSnapshot()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "import-session.ansight.zip");
        var frame = new SessionImageFrame
        {
            FrameId = "frame-001",
            CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T06:00:05Z"),
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 5
        };
        var appIconBytes = new byte[] { 9, 8, 7 };
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "app-001",
            AppId = "com.example.imported",
            ClientName = "Imported Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.Parse("2026-04-01T06:00:00Z"),
            ConfigId = "config-001",
            ProcessSessionId = "proc-001",
            Status = "WebSocket Closed",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-04-01T06:01:00Z"),
            IsHistorical = false,
            IsPinned = true,
            SdkVersion = "0.1.0-import",
            Tags = ["import", "archive"],
            Notes = "Imported from a zip archive.",
            AppState = AppLifecycleState.Background,
            AppStateChangedUtc = DateTimeOffset.Parse("2026-04-01T06:00:30Z"),
            AppIcon = new SessionAppIcon
            {
                FileName = "app-icon.png",
                Format = "png",
                MimeType = "image/png",
                Width = 2,
                Height = 2,
                ByteCount = appIconBytes.Length
            },
            Images = [frame],
            Logs =
            [
                new LogEntry(DateTimeOffset.Parse("2026-04-01T06:00:10Z"), "Archive import log")
                {
                    Source = "Client"
                }
            ],
            MetricChannels =
            [
                new SessionMetricChannel
                {
                    ChannelId = 1,
                    Name = "FPS",
                    ColorHex = "#00FF00"
                }
            ],
            Metrics =
            [
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = 60,
                    CapturedAtUtc = DateTimeOffset.Parse("2026-04-01T06:00:20Z"),
                    SegmentId = 2
                }
            ]
        };

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using (var entryStream = sessionEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    entryStream,
                    new SessionCaptureDocument
                    {
                        Schema = SessionCaptureDocument.LegacySchemaName,
                        SavedAtUtc = DateTimeOffset.UtcNow,
                        Session = snapshot
                    },
                    protocolJson);
            }

            var imageEntry = archive.CreateEntry(SessionImageArtifactPath.ResolveArchiveEntryPath(frame));
            await using (var imageStream = imageEntry.Open())
            {
                await imageStream.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
            }

            var touchesEntry = archive.CreateEntry("session-data/touches.json");
            await using (var touchesStream = touchesEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    touchesStream,
                    new JsonObject
                    {
                        ["schema"] = "ansight.touches.v1",
                        ["savedAtUtc"] = DateTimeOffset.UtcNow,
                        ["batches"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["t0"] = DateTimeOffset.Parse("2026-04-01T06:00:25Z"),
                                ["space"] = "w",
                                ["unit"] = "pt",
                                ["surface"] = new JsonArray(440, 275, 2),
                                ["rows"] = new JsonArray
                                {
                                    new JsonArray(0, 0, 7, 44, 55)
                                }
                            }
                        }
                    },
                    protocolJson);
            }

            var appIconEntry = archive.CreateEntry(SessionAppIconArtifactPath.ResolveArchiveEntryPath(snapshot.AppIcon));
            await using (var appIconStream = appIconEntry.Open())
            {
                await appIconStream.WriteAsync(appIconBytes);
            }
        }

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        Assert.True(importResult.IsSuccess);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal("com.example.imported", importResult.ImportedSession!.AppId);
        Assert.True(importResult.ImportedSession.IsHistorical);
        Assert.True(importResult.ImportedSession.IsPinned);
        Assert.Equal("0.1.0-import", importResult.ImportedSession.SdkVersion);
        Assert.NotNull(importResult.ImportedSession.AppIcon);
        Assert.Equal(["import", "archive"], importResult.ImportedSession.Tags);
        Assert.Single(importResult.ImportedSession.Images);
        Assert.Single(importResult.ImportedSession.Logs);
        Assert.Single(importResult.ImportedSession.Metrics);
        var importedTouch = Assert.Single(importResult.ImportedSession.Touches);
        Assert.Equal("down", importedTouch.Action);
        Assert.Equal(DateTimeOffset.Parse("2026-04-01T06:00:25Z"), importedTouch.CapturedAtUtc);
        Assert.Equal("points", importedTouch.CoordinateUnit);

        var restoredSession = Assert.Single(runtime.Sessions.GetSummaries());
        Assert.Equal(importResult.ImportedSession.SessionId, restoredSession.SessionId);
        Assert.Equal("proc-001", restoredSession.ProcessSessionId);
        Assert.Equal("0.1.0-import", restoredSession.SdkVersion);
        Assert.NotNull(restoredSession.AppIcon);
        var importedIconPath = SessionAppIconArtifactPath.ResolveSessionIconPath(
            SessionAppIconArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths),
            restoredSession.AppId,
            restoredSession.SessionId,
            restoredSession.AppIcon!);
        Assert.True(File.Exists(importedIconPath));
        Assert.Equal(appIconBytes, File.ReadAllBytes(importedIconPath));
        Assert.Single(restoredSession.Images);
        Assert.Empty(restoredSession.Logs);
        Assert.Empty(restoredSession.Metrics);
        Assert.True(runtime.Sessions.TryGetSnapshot(restoredSession.SessionId, out var restoredSnapshot));
        Assert.NotNull(restoredSnapshot);
        Assert.Single(restoredSnapshot!.Logs);
        Assert.Single(restoredSnapshot.Metrics);
        Assert.Single(restoredSnapshot.Touches);
    }

    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_RestoresVersionTwoExternalVisualTrees()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "import-session-v2.ansight.zip");
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-25T01:02:03Z");
        var visualTree = new SessionVisualTreeSnapshot
        {
            SnapshotId = "tree-001",
            CapturedAtUtc = capturedAtUtc,
            Source = "test",
            NodeCount = 1,
            Payload = new JsonObject
            {
                ["root"] = new JsonObject
                {
                    ["id"] = "root",
                    ["type"] = "Page"
                }
            }
        };
        var visualTreeEntryPath = "visual-trees/tree-001.json";
        var header = SessionVisualTreeSnapshotHeader.Create(visualTree);
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "v2-session-001",
            AppId = "com.example.v2",
            ClientName = "V2 Import",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedAtUtc.AddSeconds(-1),
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = capturedAtUtc,
            IsHistorical = true,
            MetricChannels = [],
            Metrics = []
        };

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using (var entryStream = sessionEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    entryStream,
                    new SessionCaptureDocument
                    {
                        SavedAtUtc = capturedAtUtc,
                        Session = snapshot,
                        VisualTreeSnapshotIndex =
                        [
                            new SessionVisualTreeSnapshotIndexEntry
                            {
                                EntryPath = visualTreeEntryPath,
                                Header = header
                            }
                        ]
                    },
                    protocolJson);
            }

            var visualTreeEntry = archive.CreateEntry(visualTreeEntryPath);
            await using var visualTreeStream = visualTreeEntry.Open();
            await JsonSerializer.SerializeAsync(
                visualTreeStream,
                new SessionVisualTreeSnapshotDocument
                {
                    SavedAtUtc = capturedAtUtc,
                    Header = header,
                    Snapshot = visualTree
                },
                protocolJson);
        }

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        Assert.True(importResult.IsSuccess, importResult.Message);
        var importedTree = Assert.Single(importResult.ImportedSession!.VisualTreeSnapshots);
        Assert.Equal("tree-001", importedTree.SnapshotId);
        Assert.Equal(1, importedTree.NodeCount);

        var exportedArchivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "export-session-v2.ansight.zip");
        var exportResult = await runtime.SessionArchives.ExportSessionArchiveAsync(importResult.ImportedSession.SessionId, exportedArchivePath);
        Assert.True(exportResult.IsSuccess, exportResult.Message);
        using var exportedArchive = ZipFile.OpenRead(exportedArchivePath);
        var exportedSessionEntry = exportedArchive.GetEntry("session.json");
        Assert.NotNull(exportedSessionEntry);
        using var exportedSessionStream = exportedSessionEntry!.Open();
        using var exportedSessionReader = new StreamReader(exportedSessionStream);
        var exportedSessionJson = await exportedSessionReader.ReadToEndAsync();
        Assert.DoesNotContain('\n', exportedSessionJson);
        var exportedDocument = JsonSerializer.Deserialize<SessionCaptureDocument>(exportedSessionJson, protocolJson);
        Assert.NotNull(exportedDocument);
        Assert.Equal(SessionCaptureDocument.SchemaName, exportedDocument.Schema);
        Assert.Empty(exportedDocument.Session.VisualTreeSnapshots);
        var exportedTreeDescriptor = Assert.Single(exportedDocument.VisualTreeSnapshotIndex);
        Assert.NotNull(exportedArchive.GetEntry(exportedTreeDescriptor.EntryPath));
    }

    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_RestoresJsonLinesArchivePayloads()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "import-session-jsonl.ansight.zip");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "jsonl-archive-001",
            AppId = "com.example.jsonl.archive",
            ClientName = "JSONL Archive Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.Parse("2026-04-01T06:00:00Z"),
            ConfigId = "config-jsonl",
            Status = "Imported",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-04-01T06:01:00Z"),
            IsHistorical = false,
            Logs = [],
            MetricChannels = [],
            Metrics = []
        };

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var sessionEntry = archive.CreateEntry("session.json");
            await using (var entryStream = sessionEntry.Open())
            {
                await JsonSerializer.SerializeAsync(
                    entryStream,
                    new SessionCaptureDocument
                    {
                        SavedAtUtc = DateTimeOffset.UtcNow,
                        Session = snapshot
                    },
                    protocolJson);
            }

            await WriteArchiveTextEntryAsync(
                archive,
                "session-data/logs.jsonl",
                JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.Parse("2026-04-01T06:00:10Z"),
                    message = "JSONL archive log",
                    source = "Client",
                    tag = "Import"
                }, protocolJson));
            await WriteArchiveTextEntryAsync(
                archive,
                "session-data/metric-channels.jsonl",
                JsonSerializer.Serialize(new
                {
                    id = 1,
                    name = "FPS",
                    color = "#00ff00"
                }, protocolJson));
            await WriteArchiveTextEntryAsync(
                archive,
                "session-data/metrics.jsonl",
                JsonSerializer.Serialize(new
                {
                    channel = 1,
                    value = 61,
                    capturedAtUtc = DateTimeOffset.Parse("2026-04-01T06:00:20Z")
                }, protocolJson));
            await WriteArchiveTextEntryAsync(
                archive,
                "session-data/touches.jsonl",
                JsonSerializer.Serialize(new
                {
                    schema = "ansight.touches.v1",
                    t0 = DateTimeOffset.Parse("2026-04-01T06:00:25Z"),
                    space = "w",
                    unit = "pt",
                    surface = new object?[] { 440, 275, 2 },
                    rows = new object?[]
                    {
                        new object?[] { 0, 0, 7, 44, 55 }
                    }
                }, protocolJson));
        }

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        Assert.True(importResult.IsSuccess, importResult.Message);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal("com.example.jsonl.archive", importResult.ImportedSession!.AppId);
        var importedLog = Assert.Single(importResult.ImportedSession.Logs);
        Assert.Equal("JSONL archive log", importedLog.Message);
        var importedChannel = Assert.Single(importResult.ImportedSession.MetricChannels);
        Assert.Equal((byte)1, importedChannel.ChannelId);
        Assert.Equal("#00FF00", importedChannel.ColorHex);
        var importedMetric = Assert.Single(importResult.ImportedSession.Metrics);
        Assert.Equal(61, importedMetric.Value);
        var importedTouch = Assert.Single(importResult.ImportedSession.Touches);
        Assert.Equal("down", importedTouch.Action);
        Assert.Equal("points", importedTouch.CoordinateUnit);
    }

    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_RestoresStandaloneOfflineJsonLinesCapture()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var jsonLinesPath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "offline-capture.jsonl");
        var imageBytes = new byte[] { 1, 2, 3, 4, 5 };
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "offline-jsonl-001",
            AppId = "com.example.offline.jsonl",
            ClientName = "Offline JSONL Client",
            RemoteAddress = "offline",
            CreatedUtc = DateTimeOffset.Parse("2026-04-02T06:00:00Z"),
            ConfigId = null,
            Status = "Offline",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-04-02T06:01:00Z"),
            IsHistorical = true,
            Logs = [],
            MetricChannels = [],
            Metrics = []
        };
        var jsonLines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", session = snapshot }, protocolJson),
            JsonSerializer.Serialize(new
            {
                type = "CLIENT_METRIC_CHANNELS",
                channels = new[]
                {
                    new
                    {
                        id = 1,
                        name = "FPS",
                        color = "#00ff00"
                    }
                }
            }, protocolJson),
            JsonSerializer.Serialize(new
            {
                type = "CLIENT_METRICS",
                metrics = new[]
                {
                    new
                    {
                        channel = 1,
                        value = 59,
                        capturedAtUtc = DateTimeOffset.Parse("2026-04-02T06:00:20Z")
                    }
                }
            }, protocolJson),
            JsonSerializer.Serialize(new
            {
                type = "CLIENT_TOUCH_INPUT",
                schema = "ansight.touches.v1",
                t0 = DateTimeOffset.Parse("2026-04-02T06:00:25Z"),
                space = "w",
                unit = "pt",
                surface = new object?[] { 440, 275, 2 },
                rows = new object?[]
                {
                    new object?[] { 0, 0, 7, 44, 55 }
                }
            }, protocolJson),
            JsonSerializer.Serialize(new
            {
                type = "CLIENT_LOG",
                data = "Offline JSONL log",
                capturedAtUtc = DateTimeOffset.Parse("2026-04-02T06:00:30Z")
            }, protocolJson),
            JsonSerializer.Serialize(new
            {
                type = "CLIENT_JPEG",
                frameId = "frame-offline",
                capturedAtUtc = DateTimeOffset.Parse("2026-04-02T06:00:40Z"),
                format = "jpeg",
                width = 1,
                height = 1,
                quality = 80,
                base64 = Convert.ToBase64String(imageBytes)
            }, protocolJson)
        };
        await File.WriteAllLinesAsync(jsonLinesPath, jsonLines);

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(jsonLinesPath);

        Assert.True(importResult.IsSuccess, importResult.Message);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal("offline-jsonl-001", importResult.ImportedSession!.SessionId);
        Assert.Equal("com.example.offline.jsonl", importResult.ImportedSession.AppId);
        Assert.Single(importResult.ImportedSession.Logs);
        Assert.Single(importResult.ImportedSession.MetricChannels);
        Assert.Single(importResult.ImportedSession.Metrics);
        Assert.Single(importResult.ImportedSession.Touches);
        var importedFrame = Assert.Single(importResult.ImportedSession.Images);
        var importedImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths),
            importResult.ImportedSession.AppId,
            importResult.ImportedSession.SessionId,
            importedFrame);
        Assert.True(File.Exists(importedImagePath));
        Assert.Equal(imageBytes, File.ReadAllBytes(importedImagePath));
    }

    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_RestoresOfflineCaptureSdkArchive()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "offline-sdk-capture.zip");
        const string sessionId = "offline-sdk-001";
        const string archiveRoot = ".ansight/sessions/offline-sdk-001/";
        var imageBytes = new byte[] { 7, 6, 5, 4, 3 };
        var screenshotUtc = DateTimeOffset.Parse("2026-04-03T06:00:40Z");
        var screenshotRelativePath = "screenshots/20260403060040000.jpg";

        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "manifest.json",
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    SessionId = sessionId,
                    StartedAtUtc = DateTimeOffset.Parse("2026-04-03T06:00:00Z"),
                    StoppedAtUtc = DateTimeOffset.Parse("2026-04-03T06:01:00Z"),
                    AppId = "com.example.offline.sdk",
                    ClientName = "Offline SDK App",
                    RemoteAddress = "offline",
                    ProcessSessionId = "12345",
                    SdkVersion = "1.0.2-preview.1",
                    AppState = 1,
                    AppStateChangedUtc = DateTimeOffset.Parse("2026-04-03T06:00:10Z")
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "metadata/channels.json",
                JsonSerializer.Serialize(new
                {
                    v = 1,
                    ch = new[]
                    {
                        new
                        {
                            id = 1,
                            n = "FPS",
                            c = "#00ff00"
                        }
                    }
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "metadata/custom-properties.json",
                JsonSerializer.Serialize(new
                {
                    scenario = new
                    {
                        mode = "offline"
                    }
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "telemetry/metrics/m-20260403060020000.jsonl",
                JsonSerializer.Serialize(new
                {
                    t = DateTimeOffset.Parse("2026-04-03T06:00:20Z").ToUnixTimeMilliseconds(),
                    c = 1,
                    v = 72
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "telemetry/events/e-20260403060025000.jsonl",
                JsonSerializer.Serialize(new
                {
                    id = "11111111111111111111111111111111",
                    t = DateTimeOffset.Parse("2026-04-03T06:00:25Z").ToUnixTimeMilliseconds(),
                    c = 0,
                    k = 3,
                    l = "export_warning",
                    d = "needs-review"
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "input/touches/t-20260403060030000.jsonl",
                JsonSerializer.Serialize(new
                {
                    id = "22222222222222222222222222222222",
                    t = DateTimeOffset.Parse("2026-04-03T06:00:30Z").ToUnixTimeMilliseconds(),
                    a = 0,
                    p = 7,
                    x = 44,
                    y = 55,
                    w = 440,
                    h = 275,
                    u = "points",
                    s = 2
                }));
            await WriteArchiveTextEntryAsync(
                archive,
                archiveRoot + "screenshots/index/s-20260403060040000.jsonl",
                JsonSerializer.Serialize(new
                {
                    t = screenshotUtc.ToUnixTimeMilliseconds(),
                    p = screenshotRelativePath,
                    w = 1,
                    h = 1,
                    q = 80,
                    b = imageBytes.Length
                }));

            var imageEntry = archive.CreateEntry(archiveRoot + screenshotRelativePath);
            await using var imageStream = imageEntry.Open();
            await imageStream.WriteAsync(imageBytes);
        }

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        Assert.True(importResult.IsSuccess, importResult.Message);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal(sessionId, importResult.ImportedSession!.SessionId);
        Assert.Equal("com.example.offline.sdk", importResult.ImportedSession.AppId);
        Assert.Equal("Offline SDK App", importResult.ImportedSession.ClientName);
        Assert.Equal("1.0.2-preview.1", importResult.ImportedSession.SdkVersion);
        Assert.Equal("12345", importResult.ImportedSession.ProcessSessionId);
        var channel = Assert.Single(importResult.ImportedSession.MetricChannels);
        Assert.Equal("FPS", channel.Name);
        Assert.Equal("#00FF00", channel.ColorHex);
        var metric = Assert.Single(importResult.ImportedSession.Metrics);
        Assert.Equal(72, metric.Value);
        var log = Assert.Single(importResult.ImportedSession.Logs);
        Assert.Equal("export_warning: needs-review", log.Message);
        Assert.Equal("WARNING", log.Tag);
        var touch = Assert.Single(importResult.ImportedSession.Touches);
        Assert.Equal("down", touch.Action);
        Assert.Equal(7, touch.PointerId);
        Assert.Equal("points", touch.CoordinateUnit);
        var frame = Assert.Single(importResult.ImportedSession.Images);
        Assert.Equal(screenshotUtc, frame.CapturedAtUtc);
        var imagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            SessionImageArtifactPath.ResolveSessionCapturesRootPath(runtime.ApplicationPaths),
            importResult.ImportedSession.AppId,
            importResult.ImportedSession.SessionId,
            frame);
        Assert.True(File.Exists(imagePath));
        Assert.Equal(imageBytes, File.ReadAllBytes(imagePath));
    }

    [Fact(Timeout = 60000)]
    public async Task ImportSessionArchiveAsync_ImportsPasswordProtectedOfflineCaptureSdkArchive()
    {
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "encrypted-offline-sdk-capture.zip");
        const string password = "correct horse battery staple";
        const string sessionId = "encrypted-offline-sdk-001";
        const string archiveRoot = ".ansight/sessions/encrypted-offline-sdk-001/";

        WriteEncryptedArchive(
            archivePath,
            password,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [archiveRoot + "manifest.json"] = JsonSerializer.Serialize(new
                {
                    Version = 1,
                    SessionId = sessionId,
                    StartedAtUtc = DateTimeOffset.Parse("2026-04-04T06:00:00Z"),
                    StoppedAtUtc = DateTimeOffset.Parse("2026-04-04T06:01:00Z"),
                    AppId = "com.example.offline.encrypted",
                    ClientName = "Encrypted Offline SDK App",
                    RemoteAddress = "offline",
                    SdkVersion = "1.0.2-preview.3"
                }),
                [archiveRoot + "telemetry/events/e-20260404060025000.jsonl"] = JsonSerializer.Serialize(new
                {
                    id = "33333333333333333333333333333333",
                    t = DateTimeOffset.Parse("2026-04-04T06:00:25Z").ToUnixTimeMilliseconds(),
                    c = 0,
                    k = 3,
                    l = "encrypted_export",
                    d = "imported"
                })
            });

        var passwordRequiredResult = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);

        Assert.False(passwordRequiredResult.IsSuccess);
        Assert.Equal(SessionImportFailureReason.PasswordRequired, passwordRequiredResult.FailureReason);

        var incorrectPasswordResult = await runtime.SessionArchives.ImportSessionArchiveAsync(
            archivePath,
            password: "incorrect");

        Assert.False(incorrectPasswordResult.IsSuccess);
        Assert.Equal(SessionImportFailureReason.InvalidPassword, incorrectPasswordResult.FailureReason);

        var importResult = await runtime.SessionArchives.ImportSessionArchiveAsync(
            archivePath,
            password: password);

        Assert.True(importResult.IsSuccess, importResult.Message);
        Assert.NotNull(importResult.ImportedSession);
        Assert.Equal(sessionId, importResult.ImportedSession!.SessionId);
        Assert.Equal("com.example.offline.encrypted", importResult.ImportedSession.AppId);
        var log = Assert.Single(importResult.ImportedSession.Logs);
        Assert.Equal("encrypted_export: imported", log.Message);
    }
}
