using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class HostSessionReplayServiceTests
{
    [Fact(Timeout = 30000)]
    public async Task ImportedSessionReplaysLocallyWithoutCloudAccess()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var archivePath = Path.Combine(environment.RootPath, "local-replay.zip");
        var frameBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var capturedAtUtc = DateTimeOffset.Parse("2026-08-17T01:00:00Z");
        var frame = new SessionImageFrame
        {
            FrameId = "frame-local-001",
            CapturedAtUtc = capturedAtUtc,
            Format = "png",
            Width = 1,
            Height = 1,
            Quality = 100,
            ByteCount = frameBytes.Length
        };
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "local-replay-001",
            AppId = "com.example.local-replay",
            ClientName = "Local Replay",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedAtUtc,
            LastUpdatedUtc = capturedAtUtc,
            ConfigId = null,
            Status = "Complete",
            IsHistorical = true,
            Images = [frame],
            MetricChannels = [],
            Metrics = [],
            AppIcon = new SessionAppIcon
            {
                FileName = "app-icon.png",
                Format = "png",
                MimeType = "image/png",
                Width = 1,
                Height = 1,
                ByteCount = frameBytes.Length
            }
        };
        await WriteArchiveAsync(archivePath, snapshot, frame, frameBytes);

        var import = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        var imported = Assert.IsType<AppSessionSnapshot>(import.ImportedSession);
        var started = await runtime.SessionReplays.StartAsync(new SessionReplayStartRequest(imported.SessionId));

        Assert.True(started.IsSuccess, started.Message);
        var replayUrl = Assert.IsType<Uri>(started.ReplayUrl);
        Assert.Equal("127.0.0.1", replayUrl.Host);
        Assert.NotEqual("/", replayUrl.AbsolutePath);
        using var client = new HttpClient();
        using var page = await client.GetAsync(replayUrl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Ansight Local Replay", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(page.Headers.Contains("Content-Security-Policy"));
        Assert.False(page.Headers.Contains("Access-Control-Allow-Origin"));

        var session = JsonNode.Parse(await client.GetStringAsync(new Uri(replayUrl, "api/session")))!.AsObject();
        Assert.Equal(imported.SessionId, session["sessionId"]?.GetValue<string>());
        Assert.Equal(frameBytes, await client.GetByteArrayAsync(
            new Uri(replayUrl, $"frames/{Uri.EscapeDataString(frame.FrameId)}")));

        using var shareRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(replayUrl, $"api/sessions/{Uri.EscapeDataString(imported.SessionId)}/share"))
        {
            Content = JsonContent.Create(new { teamId = Guid.NewGuid(), accessLevel = "team" })
        };
        using var share = await client.SendAsync(shareRequest);
        Assert.Equal(HttpStatusCode.Conflict, share.StatusCode);
        Assert.Contains("optional", await share.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        var explorerUrl = Assert.IsType<Uri>(explorer.ExplorerUrl);
        var bootstrap = JsonNode.Parse(await client.GetStringAsync(new Uri(explorerUrl, "api/bootstrap")))!.AsObject();
        Assert.Equal("explorer", bootstrap["mode"]?.GetValue<string>());
        Assert.True(bootstrap["supportsSessionAdministration"]?.GetValue<bool>());
        Assert.False(bootstrap["supportsCloudSessions"]?.GetValue<bool>());
        Assert.False(bootstrap["supportsAccountManagement"]?.GetValue<bool>());
        var settings = JsonNode.Parse(await client.GetStringAsync(new Uri(explorerUrl, "api/settings")))!.AsObject();
        Assert.NotNull(settings["logCaptureLevel"]);
        using var settingsResponse = await client.PostAsync(
            new Uri(explorerUrl, "api/settings"),
            JsonContent.Create(new CoreSettingsUpdateRequest(
                "Debug", false, false, false, null, null, false, 14, 7,
                1024L * 1024L * 1024L, 20, 256, null, null, "disabled")));
        Assert.Equal(HttpStatusCode.OK, settingsResponse.StatusCode);
        var savedSettings = JsonNode.Parse(await settingsResponse.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("Debug", savedSettings["settings"]?["logCaptureLevel"]?.GetValue<string>());
        var storageSettingsUrl = new Uri(explorerUrl, "api/settings/session-storage");
        var storageSettings = JsonNode.Parse(await client.GetStringAsync(storageSettingsUrl))!.AsObject();
        Assert.False(storageSettings["sessionAutoCleanupEnabled"]?.GetValue<bool>());
        using var storageSettingsResponse = await client.PostAsync(
            storageSettingsUrl,
            JsonContent.Create(new SessionStorageSettings(true, 30, 15, 2L * 1024 * 1024 * 1024)));
        Assert.Equal(HttpStatusCode.OK, storageSettingsResponse.StatusCode);
        var updatedStorageSettings = JsonNode.Parse(await client.GetStringAsync(storageSettingsUrl))!.AsObject();
        Assert.True(updatedStorageSettings["sessionAutoCleanupEnabled"]?.GetValue<bool>());
        Assert.Equal(30, updatedStorageSettings["sessionAutoCleanupRetentionDays"]?.GetValue<int>());
        Assert.Equal(15, updatedStorageSettings["sessionAutoCompactionAgeDays"]?.GetValue<int>());
        Assert.Equal(2L * 1024 * 1024 * 1024, updatedStorageSettings["sessionAutoCleanupMaximumCacheBytes"]?.GetValue<long>());
        var unchangedCoreSettings = JsonNode.Parse(await client.GetStringAsync(new Uri(explorerUrl, "api/settings")))!.AsObject();
        Assert.Equal("Debug", unchangedCoreSettings["logCaptureLevel"]?.GetValue<string>());
        var cachePlan = JsonNode.Parse(await client.GetStringAsync(new Uri(explorerUrl, "api/session-cache")))!.AsObject();
        Assert.Equal(2L * 1024 * 1024 * 1024, cachePlan["maximumCacheSizeBytes"]?.GetValue<long>());
        var storage = JsonNode.Parse(await client.GetStringAsync(
            new Uri(replayUrl, $"api/sessions/{Uri.EscapeDataString(imported.SessionId)}/storage")))!.AsObject();
        Assert.True(storage["totalSizeBytes"]!.GetValue<long>() > 0);
        var updates = JsonNode.Parse(await client.GetStringAsync(
            new Uri(replayUrl, $"api/sessions/{Uri.EscapeDataString(imported.SessionId)}/updates?imageIndex=0")))!.AsObject();
        Assert.False(updates["requiresReset"]!.GetValue<bool>());
        Assert.Single(updates["images"]!.AsArray());
    }

    private static async Task WriteArchiveAsync(
        string archivePath,
        AppSessionSnapshot snapshot,
        SessionImageFrame frame,
        byte[] frameBytes)
    {
        using var stream = File.Create(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var sessionEntry = archive.CreateEntry("session.json");
        await using (var sessionStream = sessionEntry.Open())
        {
            await JsonSerializer.SerializeAsync(sessionStream, new SessionCaptureDocument
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Session = snapshot
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = JsonUtil.MaximumDepth });
        }

        var frameEntry = archive.CreateEntry(SessionImageArtifactPath.ResolveArchiveEntryPath(frame));
        await using (var frameStream = frameEntry.Open())
        {
            await frameStream.WriteAsync(frameBytes);
        }

        var iconEntry = archive.CreateEntry(SessionAppIconArtifactPath.ResolveArchiveEntryPath(snapshot.AppIcon!));
        await using var iconStream = iconEntry.Open();
        await iconStream.WriteAsync(frameBytes);
    }
}
