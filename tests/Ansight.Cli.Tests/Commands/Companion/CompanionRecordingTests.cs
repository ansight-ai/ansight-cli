using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net;
using System.Text.Json;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.Agent;
using Ansight.RemoteSimulator.Core.Annotations;
using Ansight.RemoteSimulator.Core.AppInspection;
using SkiaSharp;
using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionRecordingTests
{
    [Fact]
    public async Task TrendsReview_IncludesConfiguredDisplayTitlesForTheSelectedApp()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var workspace = Path.Combine(directory.Path, "workspace");
        var trendsPath = Path.Combine(workspace, "ansight", "trends");
        Directory.CreateDirectory(trendsPath);
        File.WriteAllText(Path.Combine(trendsPath, "loading.json"), """
            {
              "schemaVersion": 1,
              "id": "loading",
              "appId": "com.example.checkout",
              "span": {
                "start": { "event": { "label": "loading.started" } },
                "end": { "event": { "label": "loading.completed" } }
              },
              "metrics": [{
                "id": "memory-average",
                "channel": { "type": "memory" },
                "statistic": "average",
                "budget": { "lte": 200 },
                "regression": { "percent": 10, "confirmRuns": 2 },
                "display": { "title": "Memory while loading", "unit": "MiB" }
              }]
            }
            """);
        var registration = runtime.Apps.Register(new AppRegistrationRequest("com.example.checkout", "Checkout", workspace));
        Assert.True(registration.IsSuccess);
        var source = new CliRemoteRecordingSource(runtime);

        var response = await source.ReviewAsync("trends.history", null,
            JsonSerializer.SerializeToElement(new { appId = "com.example.checkout" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(response.Content);
        var chart = Assert.Single(json.RootElement.GetProperty("charts").EnumerateArray());
        Assert.Equal("Memory while loading", chart.GetProperty("title").GetString());
        Assert.NotEmpty(chart.GetProperty("decisionId").GetString()!);
        Assert.NotEmpty(chart.GetProperty("definitionHash").GetString()!);

        var otherApp = await source.ReviewAsync("trends.history", null,
            JsonSerializer.SerializeToElement(new { appId = "com.example.other" }));
        using var otherJson = JsonDocument.Parse(otherApp.Content);
        Assert.Empty(otherJson.RootElement.GetProperty("charts").EnumerateArray());
    }

    [Fact]
    public async Task SavedRecordings_ListDetailsFrameAndBoundedEvidence()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportAsync(runtime, directory.Path);
        var source = new CliRemoteRecordingSource(runtime);

        var summary = Assert.Single(await source.ListAsync());
        Assert.Equal(session.SessionId, summary.Id);
        Assert.Equal("Checkout test", summary.Title);
        Assert.False(summary.IsLive);
        Assert.True(summary.Capabilities.HasScreenReplay);

        var details = Assert.IsType<Ansight.RemoteSimulator.Core.Recording.RemoteRecordingDetails>(await source.GetAsync(session.SessionId));
        Assert.Equal("frame-1", Assert.Single(details.Frames).Id);
        var frame = await source.GetFrameAsync(session.SessionId, "frame-1");
        Assert.NotNull(frame);
        Assert.Equal("image/png", frame.ContentType);
        Assert.NotEmpty(frame.Content);
        Assert.Null(await source.GetFrameAsync(session.SessionId, "../frame-1"));
        Assert.Null(await source.GetAsync("missing"));

        var evidence = await source.GetEvidenceAsync(session.SessionId, session.CreatedUtc, session.LastUpdatedUtc, 1, 1);
        Assert.NotNull(evidence);
        Assert.Single(evidence.Logs);
        Assert.True(evidence.LogsTruncated);
        Assert.Single(evidence.Annotations);
        Assert.Single(evidence.TelemetrySamples);
        Assert.True(evidence.TelemetryTruncated);
        Assert.Equal(42, evidence.TelemetrySamples[0].Value);
    }

    [Fact]
    public async Task Evidence_SelectsNearestCursorAndFiltersStreamBeforeLimiting()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportAsync(runtime, directory.Path);
        var source = new CliRemoteRecordingSource(runtime);
        var cursor = session.CreatedUtc.AddSeconds(1);

        var evidence = await source.GetEvidenceAsync(new RemoteRecordingEvidenceQuery(
            session.SessionId, session.CreatedUtc, session.LastUpdatedUtc, cursor, 1, 1, "system"));

        Assert.NotNull(evidence);
        Assert.Equal("System", Assert.Single(evidence.Logs).Message);
        Assert.False(evidence.LogsTruncated);
        Assert.Equal(43.25, Assert.Single(evidence.TelemetrySamples).NumericValue);
        Assert.Equal("Checkout", Assert.Single(evidence.Events).Label);
    }

    [Fact]
    public async Task Review_ReadsRetainedNetworkAndArtifactsInVersionedChunks()
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportAsync(runtime, directory.Path);
        var source = new CliRemoteRecordingSource(runtime);
        var empty = JsonSerializer.SerializeToElement(new { });

        var network = await source.ReviewAsync("network.list", session.SessionId, empty);
        Assert.Equal(HttpStatusCode.OK, network.StatusCode);
        using var networkJson = JsonDocument.Parse(network.Content);
        Assert.Equal("request-1", networkJson.RootElement.GetProperty("requests")[0].GetProperty("id").GetString());

        var details = await source.ReviewAsync("network.details", session.SessionId, JsonSerializer.SerializeToElement(new { id = "request-1" }));
        using var detailsJson = JsonDocument.Parse(details.Content);
        Assert.Equal("{\"ok\":true}", detailsJson.RootElement.GetProperty("responseBody").GetProperty("data").GetString());

        var artifacts = await source.ReviewAsync("artifacts.list", session.SessionId, empty);
        using var artifactJson = JsonDocument.Parse(artifacts.Content);
        Assert.Equal("settings.json", artifactJson.RootElement[0].GetProperty("entries")[0].GetProperty("relativePath").GetString());

        var first = await source.ReviewAsync("artifacts.download", session.SessionId,
            JsonSerializer.SerializeToElement(new { root = "artifact-1", path = "settings.json", maxBytes = 5 }));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(first.Content);
        var version = firstJson.RootElement.GetProperty("version").GetString();
        Assert.True(firstJson.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(5, firstJson.RootElement.GetProperty("nextOffsetBytes").GetInt64());

        var second = await source.ReviewAsync("artifacts.download", session.SessionId,
            JsonSerializer.SerializeToElement(new { root = "artifact-1", path = "settings.json", offsetBytes = 5, expectedVersion = version }));
        using var secondJson = JsonDocument.Parse(second.Content);
        var bytes = Convert.FromBase64String(firstJson.RootElement.GetProperty("base64").GetString()!)
            .Concat(Convert.FromBase64String(secondJson.RootElement.GetProperty("base64").GetString()!)).ToArray();
        Assert.Equal("{\"ok\":true}", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.False(secondJson.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData("artifacts.download", "{\"root\":\"artifact-1\",\"path\":\"../storage.json\"}", HttpStatusCode.NotFound)]
    [InlineData("artifacts.download", "{\"root\":\"artifact-1\",\"path\":\"settings.json\",\"offsetBytes\":-1}", HttpStatusCode.BadRequest)]
    [InlineData("artifacts.download", "{\"root\":\"artifact-1\",\"path\":\"settings.json\",\"expectedVersion\":\"old\"}", HttpStatusCode.Conflict)]
    [InlineData("network.details", "{\"id\":\"missing\"}", HttpStatusCode.NotFound)]
    [InlineData("files.write", "{}", HttpStatusCode.BadRequest)]
    [InlineData("network.list", "[]", HttpStatusCode.BadRequest)]
    public async Task Review_RejectsInvalidOrMutatingRequests(string operation, string arguments, HttpStatusCode expected)
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportAsync(runtime, directory.Path);
        var source = new CliRemoteRecordingSource(runtime);
        using var json = JsonDocument.Parse(arguments);

        var result = await source.ReviewAsync(operation, session.SessionId, json.RootElement);

        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await source.ReviewAsync("network.list", "missing", JsonSerializer.SerializeToElement(new { }))).StatusCode);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("files.list")]
    [InlineData("files.download")]
    [InlineData("files.write")]
    [InlineData("files.rename")]
    [InlineData("files.delete")]
    [InlineData("tools.list")]
    [InlineData("artifacts.list")]
    [InlineData("artifacts.download")]
    public async Task InspectionCannotAccessAnUnrelatedOrRecordedSession(string operation)
    {
        using var directory = TestDirectory.Create();
        await using var runtime = CreateRuntime(directory.Path);
        var session = await ImportAsync(runtime, directory.Path);
        var source = new CliRemoteAppInspectionSource(runtime, new CliRemoteAnnotationSource(runtime));
        var request = new RemoteAppInspectionRequest("request", operation, session.SessionId, JsonSerializer.SerializeToElement(new { }));

        var response = await source.InvokeAsync("another-device", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(response.Content);
    }

    [Fact]
    public async Task AnnotationBatchCanBeEditedAndSentAfterAppDisconnects()
    {
        const string appId = "com.example.companion-annotation";
        const string deviceId = "annotation-test-device";
        const string batchId = "annotation-test-batch";
        using var directory = TestDirectory.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        int discoveryPort;

        using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            discoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        }

        await using var runtime = CreateRuntime(directory.Path, discoveryPort);
        await runtime.StartAsync();
        using var socket = await ConnectAnnotationAppAsync(runtime, appId, deviceId, discoveryPort, cancellationToken);
        var annotations = new CliRemoteAnnotationSource(runtime);
        RemoteAnnotationBatchStartResult batch;

        do
        {
            batch = await annotations.StartAnnotationBatchAsync(new(deviceId, batchId), cancellationToken);

            if (!batch.IsSuccess)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
        while (!batch.IsSuccess);

        var sessionId = Assert.IsType<string>(batch.SessionId);
        var frameId = Assert.IsType<string>(batch.FrameId);
        var disconnect = runtime.AppTools.ForceDisconnect(sessionId);

        Assert.True(disconnect.IsSuccess, disconnect.Message);
        Assert.Empty(await annotations.ListLiveSessionsAsync(cancellationToken));
        Assert.NotNull(await annotations.GetBatchFrameAsync(deviceId, batchId, sessionId, frameId, cancellationToken));
        Assert.Null(await annotations.GetBatchFrameAsync("another-device", batchId, sessionId, frameId, cancellationToken));

        var draft = new RemoteAnnotationRequest(deviceId, "point", 0.5, 0.5, null, null,
            "Check this control", "investigate", batchId, frameId);
        Assert.False((await annotations.CreateAnnotationAsync(draft with { DeviceUdid = "another-device" }, cancellationToken)).IsSuccess);

        var saved = await annotations.CreateAnnotationAsync(draft, cancellationToken);
        Assert.True(saved.IsSuccess, saved.Message);
        Assert.Equal(sessionId, saved.SessionId);
        var annotationId = Assert.IsType<string>(saved.AnnotationId);
        var updated = await annotations.UpdateAnnotationAsync(new(deviceId, annotationId, "point", 0.5, 0.5, null, null,
            "Check the control with the keyboard open", "investigate", batchId, frameId), cancellationToken);
        Assert.True(updated.IsSuccess, updated.Message);

        runtime.Apps.Register(new AppRegistrationRequest(appId, "Annotation test", directory.Path));
        var codex = new ScriptedCompanionCodex(directory.Path);
        var agent = new CliRemoteAgentChatSource(runtime, codex);
        var project = await agent.UpdateProjectAsync(new(appId, "project-1", directory.Path), cancellationToken);
        Assert.True(project.IsSuccess, project.Message);

        var submission = await agent.SubmitAnnotationBatchAsync(new(batchId, sessionId, frameId, [annotationId],
            "codex", "", ProjectId: "project-1", StartNewTask: true), cancellationToken);
        Assert.True(submission.IsSuccess, submission.Message);
        Assert.Equal("accepted", submission.Status);
        Assert.Equal("new-thread", submission.ProviderTaskId);

        var removed = await annotations.DeleteAnnotationAsync(new(deviceId, annotationId, batchId, frameId), cancellationToken);
        Assert.True(removed.IsSuccess, removed.Message);
    }

    [Fact]
    public async Task RecordingDeletionSupportsSpeechAndLaterFramesButRejectsOtherBatches()
    {
        const string appId = "com.example.recording-delete";
        const string deviceId = "annotation-test-device";
        const string batchId = "annotation-test-batch";
        using var directory = TestDirectory.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        int discoveryPort;

        using (var portProbe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            discoveryPort = ((IPEndPoint)portProbe.Client.LocalEndPoint!).Port;
        }

        await using var runtime = CreateRuntime(directory.Path, discoveryPort);
        await runtime.StartAsync();
        using var socket = await ConnectAnnotationAppAsync(runtime, appId, deviceId, discoveryPort, cancellationToken);
        var annotations = new CliRemoteAnnotationSource(runtime);
        RemoteAnnotationBatchStartResult batch;

        do
        {
            batch = await annotations.StartAnnotationBatchAsync(new(deviceId, batchId), cancellationToken);

            if (!batch.IsSuccess)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
        while (!batch.IsSuccess);

        var sessionId = Assert.IsType<string>(batch.SessionId);
        var frameId = Assert.IsType<string>(batch.FrameId);

        foreach (var (id, source, captureGroup, geometry, allowed) in new[]
        {
            ("speech", "companion.exploration", batchId, false, true),
            ("marking", "companion.exploration", batchId, true, true),
            ("foreign", "companion.exploration", "another-batch", false, false),
            ("ordinary", "simulator.remote.mobile", batchId, false, false)
        })
        {
            var annotation = new SessionAnnotation
            {
                AnnotationId = id, Source = source, CaptureGroupId = captureGroup,
                StartUtc = DateTimeOffset.UtcNow, Label = "Comment",
                Geometry = geometry ? [new SessionAnnotationGeometry
                {
                    GeometryId = "later-mark", FrameId = "later-frame", CapturedAtUtc = DateTimeOffset.UtcNow,
                    Kind = SessionAnnotationGeometryKind.Point, X = 0.5, Y = 0.5
                }] : []
            };
            Assert.True(runtime.SessionEditing.UpsertAnnotation(sessionId, annotation).IsSuccess);
            Assert.False((await annotations.DeleteAnnotationAsync(new("wrong-device", id, batchId, frameId), cancellationToken)).IsSuccess);
            var result = await annotations.DeleteAnnotationAsync(new(deviceId, id, batchId, frameId), cancellationToken);
            Assert.Equal(allowed, result.IsSuccess);
            Assert.True(runtime.Sessions.TryGetSnapshot(sessionId, out var snapshot));
            Assert.Equal(!allowed, snapshot!.Annotations.Any(item => item.AnnotationId == id));
        }
    }

    private static async Task<ClientWebSocket> ConnectAnnotationAppAsync(
        RuntimeCoordinator runtime, string appId, string deviceId, int discoveryPort, CancellationToken cancellationToken)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var invite = runtime.Pairing.Issue(appId);
        Assert.True(invite.IsSuccess, invite.Message);
        var config = runtime.Pairing.FindLocalConfig(invite.Invite!.InviteId)!.Config;
        var connect = JsonSerializer.SerializeToUtf8Bytes(new EnrollmentConnectRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            AppId = appId,
            InviteId = config.ConfigId,
            AccessToken = config.Enrollment!.Secret,
            DeviceId = deviceId,
            DeviceName = deviceId
        }, json);
        using var udp = new UdpClient();
        await udp.SendAsync(connect, new IPEndPoint(IPAddress.Loopback, discoveryPort), cancellationToken);
        var reply = await udp.ReceiveAsync(cancellationToken);
        var response = JsonSerializer.Deserialize<ConnectResponse>(reply.Buffer, json)!;
        Assert.True(response.Accepted, response.ReasonMessage);

        var socket = new ClientWebSocket();

        try
        {
            var url = new Uri($"ws://127.0.0.1:{response.WebSocketPort}{response.WebSocketPath}?token={response.WebSocketToken}");
            await socket.ConnectAsync(url, cancellationToken);
            var profile = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "DeviceAppProfile",
                device = new { nativeDeviceId = deviceId }
            }, json);
            await socket.SendAsync(profile.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
            using var bitmap = new SKBitmap(2, 2);
            using var image = SKImage.FromBitmap(bitmap);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 80);
            var payload = new byte[28 + checked((int)jpeg.Size)];
            "ASJP"u8.CopyTo(payload);
            payload[4] = 1;
            payload[5] = 1;
            payload[6] = 80;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 2);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), 2);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), (int)jpeg.Size);
            jpeg.AsSpan().CopyTo(payload.AsSpan(28));
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken);

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static RuntimeCoordinator CreateRuntime(string path, int? discoveryPort = null)
    {
        var storagePath = Path.Combine(path, "storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = path,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath,
            DiscoveryPort = discoveryPort
        });
    }

    private static async Task<AppSessionSnapshot> ImportAsync(RuntimeCoordinator runtime, string directory)
    {
        var start = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "companion-recording",
            Name = "Checkout test",
            AppId = "com.example.checkout",
            ClientName = "Checkout",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = start,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = start.AddSeconds(2),
            IsHistorical = true,
            Images = [new SessionImageFrame { FrameId = "frame-1", CapturedAtUtc = start, Format = "png", Width = 1, Height = 1, Quality = 100, ByteCount = png.Length }],
            Logs = [new LogEntry(start, "First"), new LogEntry(start.AddSeconds(1), "Second"),
                new LogEntry(start.AddSeconds(1), "System") { StreamId = "system" }],
            Annotations = [new SessionAnnotation { AnnotationId = "note", StartUtc = start, Label = "Review" }],
            MetricChannels = [new SessionMetricChannel { ChannelId = 1, Name = "CPU", ColorHex = "#FF0000" }],
            Metrics = [new SessionMetricSample { ChannelId = 1, Value = 42, CapturedAtUtc = start },
                new SessionMetricSample { ChannelId = 1, Value = 43.25, CapturedAtUtc = start.AddSeconds(1) }],
            ApplicationEvents = [new SessionApplicationEvent("event-1", "Checkout", "event", "{}", start, 1)],
            NetworkRequests = [new SessionNetworkRequest
            {
                Id = "request-1", Source = "sdk", StartedAtUtc = start, CompletedAtUtc = start.AddMilliseconds(100),
                DurationMilliseconds = 100, Method = "GET", Url = "https://example.test/items", StatusCode = 200,
                ResponseBody = new SessionNetworkBody { Encoding = "utf8", Data = "{\"ok\":true}", CapturedBytes = 11, Truncated = false }
            }],
            ArtifactSnapshots = [new SessionArtifactSnapshot
            {
                SnapshotId = "artifact-1", CapturedAtUtc = start, Source = "sdk", RootAlias = "documents", RelativePath = "settings.json",
                Name = "Settings", Kind = "file", ArtifactDirectoryName = "artifact-1", ByteCount = 11, FileCount = 1,
                Entries = [new SessionArtifactEntry
                {
                    Name = "settings.json", RootAlias = "documents", RelativePath = "settings.json", SnapshotRelativePath = "settings.json",
                    Kind = "file", SizeBytes = 11, FileExtension = "json", MimeType = "application/json", ArchiveRelativePath = "settings.json"
                }]
            }]
        };
        var path = Path.Combine(directory, "recording.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            await using (var stream = archive.CreateEntry("session.json").Open())
            {
                await JsonSerializer.SerializeAsync(stream, new SessionCaptureDocument { SavedAtUtc = start, Session = snapshot },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            await using (var stream = archive.CreateEntry("session-images/frame-1.png").Open())
            {
                await stream.WriteAsync(png);
            }

            await using (var stream = archive.CreateEntry("artifacts/artifact-1/settings.json").Open())
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"));
            }
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(path);
        Assert.True(imported.IsSuccess, imported.Message);

        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }
}
