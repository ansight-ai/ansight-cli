using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.AppInspection;
using Ansight.RemoteSimulator.Core.Recording;

namespace Ansight.Cli.Tests.Commands.Companion;

public sealed class CompanionRecordingTests
{
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

    private static RuntimeCoordinator CreateRuntime(string path)
    {
        var storagePath = Path.Combine(path, "storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(storagePath);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);

        return new RuntimeCoordinator(new RuntimeOptions
        {
            BaseFolderPath = path,
            SecureStorageFilePath = storagePath,
            SecureStorageKeyFilePath = keyPath
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
