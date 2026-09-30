using System.Net.Http.Json;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class HostArtifactMediaTests
{
    [Fact(Timeout = 30000)]
    public async Task MediaArtifactsExposeStreamMetadataAndServeSeekableContent()
    {
        using var environment = new TestEnvironment();
        var snapshot = CreateMediaSnapshot();
        new SessionCaptureStore(environment.ApplicationPaths).Save(snapshot);
        var artifactDirectory = SessionFileLocator.ResolveArtifactSnapshotDirectoryPath(
            environment.ApplicationPaths, snapshot, Assert.Single(snapshot.ArtifactSnapshots));
        Directory.CreateDirectory(artifactDirectory);
        byte[] content = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "recording.mp4"), content);
        await using var runtime = environment.CreateRuntime();
        var started = await runtime.SessionReplays.StartAsync(new SessionReplayStartRequest(snapshot.SessionId));
        Assert.True(started.IsSuccess, started.Message);
        var replayUrl = Assert.IsType<Uri>(started.ReplayUrl);
        var route = $"api/sessions/{snapshot.SessionId}/artifacts";
        var contentUri = new Uri(replayUrl, $"{route}/content?path=recording.mp4&snapshotId=media-001");
        using var client = new HttpClient();

        using var previewResponse = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/preview"),
            new { path = "recording.mp4", snapshotId = "media-001" });
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = Assert.IsType<FileVisualization>(await previewResponse.Content.ReadFromJsonAsync<FileVisualization>());
        Assert.True(preview.IsSuccess, preview.Message);
        Assert.Equal(FileViewerKinds.Video, preview.ViewerKind);
        Assert.Equal("video/mp4", preview.MimeType);
        Assert.Equal(content.Length, preview.SizeBytes);
        Assert.Equal(0, preview.BytesRead);
        Assert.False(preview.IsTruncated);
        Assert.Null(preview.Base64);
        Assert.Null(preview.Text);
        Assert.NotNull(preview.ArtifactDetails);

        using var fullResponse = await client.GetAsync(contentUri);
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        Assert.Equal("video/mp4", fullResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", fullResponse.Headers.AcceptRanges);
        Assert.Equal(content, await fullResponse.Content.ReadAsByteArrayAsync());

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, contentUri);
        headRequest.Headers.Range = new RangeHeaderValue(2, 4);
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(content.Length, headResponse.Content.Headers.ContentLength);
        Assert.Null(headResponse.Content.Headers.ContentRange);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

        await AssertRangeAsync(client, contentUri, "bytes=2-4", HttpStatusCode.PartialContent, "bytes 2-4/10", [2, 3, 4]);
        await AssertRangeAsync(client, contentUri, "bytes=7-", HttpStatusCode.PartialContent, "bytes 7-9/10", [7, 8, 9]);
        await AssertRangeAsync(client, contentUri, "bytes=-2", HttpStatusCode.PartialContent, "bytes 8-9/10", [8, 9]);
        await AssertRangeAsync(client, contentUri, "bytes=8-100", HttpStatusCode.PartialContent, "bytes 8-9/10", [8, 9]);
        await AssertRangeAsync(client, contentUri, "bytes=10-", HttpStatusCode.RequestedRangeNotSatisfiable, "bytes */10", []);
        await AssertRangeAsync(client, contentUri, "bytes=0-1,8-9", HttpStatusCode.OK, null, content);
        await AssertRangeAsync(client, contentUri, "invalid", HttpStatusCode.OK, null, content);

        using var staleRangeRequest = new HttpRequestMessage(HttpMethod.Get, contentUri);
        staleRangeRequest.Headers.Range = new RangeHeaderValue(2, 4);
        staleRangeRequest.Headers.TryAddWithoutValidation("If-Range", "\"unknown-version\"");
        using var staleRangeResponse = await client.SendAsync(staleRangeRequest);
        Assert.Equal(HttpStatusCode.OK, staleRangeResponse.StatusCode);
        Assert.Equal(content, await staleRangeResponse.Content.ReadAsByteArrayAsync());

        using var forceTextResponse = await client.PostAsJsonAsync(new Uri(replayUrl, $"{route}/preview"),
            new { path = "recording.mp4", snapshotId = "media-001", forceText = true });
        var textPreview = Assert.IsType<FileVisualization>(await forceTextResponse.Content.ReadFromJsonAsync<FileVisualization>());
        Assert.Equal(FileViewerKinds.Text, textPreview.ViewerKind);
        Assert.Equal(content.Length, textPreview.BytesRead);

        using var missingResponse = await client.GetAsync(new Uri(replayUrl, $"{route}/content?path=recording.mp4&snapshotId=wrong"));
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    private static async Task AssertRangeAsync(HttpClient client, Uri uri, string range, HttpStatusCode statusCode, string? contentRange, byte[] expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await client.SendAsync(request);
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(contentRange, response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    private static AppSessionSnapshot CreateMediaSnapshot()
    {
        var capturedUtc = DateTimeOffset.UtcNow;
        return new AppSessionSnapshot
        {
            SessionId = "artifact-media-001",
            AppId = "com.example.artifact-media",
            ClientName = "Artifact Media",
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
                    SnapshotId = "media-001",
                    CapturedAtUtc = capturedUtc,
                    Source = "test",
                    RootAlias = "app-data",
                    RelativePath = "recording.mp4",
                    Name = "Recording",
                    Kind = "file",
                    ArtifactDirectoryName = "media-001",
                    FileCount = 1,
                    ByteCount = 10,
                    Entries =
                    [
                        new SessionArtifactEntry
                        {
                            Name = "recording.mp4",
                            RootAlias = "app-data",
                            RelativePath = "recording.mp4",
                            SnapshotRelativePath = "recording.mp4",
                            ArchiveRelativePath = "recording.mp4",
                            Kind = "file",
                            FileExtension = ".mp4",
                            MimeType = "application/octet-stream",
                            SizeBytes = 10
                        }
                    ]
                }
            ]
        };
    }
}
