using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Ansight.Infrastructure.Security;
using Ansight.RemoteSimulator.Core.AppInspection;

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
            Logs = [new LogEntry(start, "First"), new LogEntry(start.AddSeconds(1), "Second")],
            Annotations = [new SessionAnnotation { AnnotationId = "note", StartUtc = start, Label = "Review" }],
            MetricChannels = [new SessionMetricChannel { ChannelId = 1, Name = "CPU", ColorHex = "#FF0000" }],
            Metrics = [new SessionMetricSample { ChannelId = 1, Value = 42, CapturedAtUtc = start },
                new SessionMetricSample { ChannelId = 1, Value = 43, CapturedAtUtc = start.AddSeconds(1) }]
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
        }

        var imported = await runtime.SessionArchives.ImportSessionArchiveAsync(path);
        Assert.True(imported.IsSuccess, imported.Message);

        return Assert.IsType<AppSessionSnapshot>(imported.ImportedSession);
    }
}
