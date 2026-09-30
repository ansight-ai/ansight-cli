using System.IO.Compression;
using System.Net.Http.Json;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class HostArtifactActionTests
{
    [Fact(Timeout = 30000)]
    public async Task ArtifactActionsRequireAnArtifactInTheRequestedSessionAndSnapshot()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var snapshot = CreateSnapshot();
        var archivePath = Path.Combine(environment.RootPath, "artifact-session.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using var content = archive.CreateEntry("session.json").Open();
            await JsonSerializer.SerializeAsync(content, new SessionCaptureDocument
            {
                SavedAtUtc = DateTimeOffset.UtcNow,
                Session = snapshot
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        var import = await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath);
        var imported = Assert.IsType<AppSessionSnapshot>(import.ImportedSession);
        var artifactDirectory = SessionFileLocator.ResolveArtifactSnapshotDirectoryPath(
            runtime.ApplicationPaths, imported, Assert.Single(imported.ArtifactSnapshots));
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "reference graph.json"), "{}");
        var outsideFile = Path.Combine(environment.RootPath, "outside.json");
        await File.WriteAllTextAsync(outsideFile, "Outside the session");
        var started = await runtime.SessionReplays.StartAsync(new SessionReplayStartRequest(imported.SessionId));
        Assert.True(started.IsSuccess, started.Message);
        var replayUrl = Assert.IsType<Uri>(started.ReplayUrl);
        var route = $"api/sessions/{Uri.EscapeDataString(imported.SessionId)}/artifacts";
        using var client = new HttpClient();

        foreach (var path in new[] { outsideFile, "../../outside.json", "missing.json" })
        {
            using var applications = await client.GetAsync(new Uri(replayUrl,
                $"{route}/applications?path={Uri.EscapeDataString(path)}&snapshotId=artifact-001"));
            Assert.Equal(HttpStatusCode.NotFound, applications.StatusCode);
            foreach (var action in new[] { "open", "reveal", "export-desktop" })
            {
                using var response = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/{action}"),
                    new { path, snapshotId = "artifact-001", applicationId = "unregistered-viewer" });
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }

        using var wrongSnapshot = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/export-desktop"),
            new { path = "reference graph.json", snapshotId = "another-snapshot" });
        Assert.Equal(HttpStatusCode.NotFound, wrongSnapshot.StatusCode);

        using var missingApplication = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/open"),
            new { path = "reference graph.json", snapshotId = "artifact-001" });
        Assert.Equal(HttpStatusCode.BadRequest, missingApplication.StatusCode);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            using var applications = await client.GetAsync(new Uri(replayUrl,
                $"{route}/applications?path=reference%20graph.json&snapshotId=artifact-001"));
            Assert.Equal(HttpStatusCode.OK, applications.StatusCode);
            var result = await applications.Content.ReadFromJsonAsync<JsonObject>();
            Assert.IsType<JsonArray>(result!["applications"]);

            using var unregisteredApplication = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/open"),
                new { path = "reference graph.json", snapshotId = "artifact-001", applicationId = outsideFile });
            Assert.Equal(HttpStatusCode.BadRequest, unregisteredApplication.StatusCode);
        }
        Assert.Equal("Outside the session", await File.ReadAllTextAsync(outsideFile));
    }

    private static AppSessionSnapshot CreateSnapshot()
    {
        var capturedUtc = DateTimeOffset.UtcNow;
        return new AppSessionSnapshot
        {
            SessionId = "artifact-actions-001",
            AppId = "com.example.artifact-actions",
            ClientName = "Artifact Actions",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = capturedUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = capturedUtc,
            IsHistorical = true,
            MetricChannels = [],
            Metrics = [],
            ArtifactSnapshots =
            [
                new SessionArtifactSnapshot
                {
                    SnapshotId = "artifact-001",
                    CapturedAtUtc = capturedUtc,
                    Source = "test",
                    RootAlias = "app-data",
                    RelativePath = "reference graph.json",
                    Name = "Reference graph",
                    Kind = "file",
                    ArtifactDirectoryName = "artifact-001",
                    FileCount = 1,
                    ByteCount = 2,
                    Entries =
                    [
                        new SessionArtifactEntry
                        {
                            Name = "reference graph.json",
                            RootAlias = "app-data",
                            RelativePath = "reference graph.json",
                            SnapshotRelativePath = "reference graph.json",
                            ArchiveRelativePath = "reference graph.json",
                            Kind = "file",
                            FileExtension = ".json",
                            MimeType = "application/json",
                            SizeBytes = 2
                        }
                    ]
                }
            ]
        };
    }
}
