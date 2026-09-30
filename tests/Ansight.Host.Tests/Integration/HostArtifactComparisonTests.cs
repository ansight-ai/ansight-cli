using System.IO.Compression;
using System.Net.Http.Json;
using Ansight.Host.Files;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class HostArtifactComparisonTests
{
    [Fact(Timeout = 60000)]
    public async Task QualifiedReferencesListFilterAndCompareReversionAcrossSessionsOverHttp()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var baseline = await Import(runtime, environment.RootPath, "baseline", "{\"x\":0}");
        var rerun = await Import(runtime, environment.RootPath, "rerun", "{\"x\":12}");
        var listed = await runtime.ArtifactComparisons.ListAsync(new(baseline.SessionId, Search: "scene", Format: "json", Limit: 1));
        var item = Assert.Single(listed.Items);
        Assert.Equal($"{baseline.SessionId}/capture", item.Reference);
        Assert.Empty((await runtime.ArtifactComparisons.ListAsync(new(baseline.SessionId, Provider: "absent"))).Items);
        var start = await runtime.SessionReplays.StartAsync(new SessionReplayStartRequest(baseline.SessionId));
        Assert.True(start.IsSuccess, start.Message);
        using var client = new HttpClient();
        var url = Assert.IsType<Uri>(start.ReplayUrl);
        using var listingResponse = await client.PostAsJsonAsync(new Uri(url, "api/artifacts/list"), new ArtifactListRequest(baseline.SessionId));
        Assert.Equal(HttpStatusCode.OK, listingResponse.StatusCode);
        var request = new ArtifactDiffRequest([new(baseline.SessionId, "capture"), new(rerun.SessionId, "capture"), new(baseline.SessionId, "capture")]);
        using var response = await client.PostAsJsonAsync(new Uri(url, "api/artifacts/diff"), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ArtifactDiffResult>())!;
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Hops.Count);
        Assert.Contains("returned to its initial content", result.Summary);
        Assert.Equal("12", Assert.Single(Assert.Single(result.Hops[0].Files).Changes).After);
        Assert.Equal("0", Assert.Single(Assert.Single(result.Hops[1].Files).Changes).After);
        using var invalid = await client.PostAsJsonAsync(new Uri(url, "api/artifacts/diff"), request with { Artifacts = [new(baseline.SessionId, "capture"), new("nonexistent", "capture")] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact(Timeout = 60000)]
    public async Task MissingPayloadKeepsBothHopsIncompleteInsteadOfBridging()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var first = await Import(runtime, environment.RootPath, "first", "{}");
        var missing = await Import(runtime, environment.RootPath, "missing", "{}");
        var path = SessionFileLocator.ResolveArtifactSnapshotDirectoryPath(runtime.ApplicationPaths, missing, Assert.Single(missing.ArtifactSnapshots));
        File.Delete(Path.Combine(path, "scene.json"));
        var result = await runtime.ArtifactComparisons.CompareAsync(new([new(first.SessionId, "capture"), new(missing.SessionId, "capture"), new(first.SessionId, "capture")]));
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.Hops.Count);
        Assert.All(result.Hops, hop => Assert.Equal("unavailable", Assert.Single(hop.Files).Status));
    }

    private static async Task<AppSessionSnapshot> Import(RuntimeCoordinator runtime, string directory, string id, string json)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new AppSessionSnapshot
        {
            SessionId = id, AppId = "test.artifacts", ClientName = id, RemoteAddress = "127.0.0.1", CreatedUtc = timestamp,
            ConfigId = null, Status = "Complete", LastUpdatedUtc = timestamp, IsHistorical = true, MetricChannels = [], Metrics = [],
            ArtifactSnapshots = [new SessionArtifactSnapshot
            {
                SnapshotId = "capture", CapturedAtUtc = timestamp, Source = "test", RootAlias = "player", RelativePath = "scene", Name = "Scene",
                Kind = "file", ArtifactDirectoryName = "capture", FileCount = 1, ByteCount = json.Length,
                Entries = [new SessionArtifactEntry { Name = "scene.json", RootAlias = "player", RelativePath = "scene", SnapshotRelativePath = "scene.json", ArchiveRelativePath = "scene.json", Kind = "file", FileExtension = ".json", MimeType = "application/json", SizeBytes = json.Length }]
            }]
        };
        var archivePath = Path.Combine(directory, id + ".zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using var content = archive.CreateEntry("session.json").Open();
            await JsonSerializer.SerializeAsync(content, new SessionCaptureDocument { SavedAtUtc = timestamp, Session = snapshot }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        var imported = Assert.IsType<AppSessionSnapshot>((await runtime.SessionArchives.ImportSessionArchiveAsync(archivePath)).ImportedSession);
        var artifactDirectory = SessionFileLocator.ResolveArtifactSnapshotDirectoryPath(runtime.ApplicationPaths, imported, Assert.Single(imported.ArtifactSnapshots));
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "scene.json"), json);
        return imported;
    }
}
