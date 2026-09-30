namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void SaveAndTryLoad_PersistsRedactedNetworkRequestDocuments()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var startedAtUtc = DateTimeOffset.Parse("2026-08-23T00:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-network",
            AppId = "com.example.network",
            ClientName = "Network Test",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = startedAtUtc,
            ConfigId = null,
            Status = "WebSocket Closed",
            LastUpdatedUtc = startedAtUtc.AddSeconds(1),
            IsHistorical = false,
            NetworkRequests =
            [
                new SessionNetworkRequest
                {
                    Id = "request-001",
                    Source = "test",
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = startedAtUtc.AddMilliseconds(125),
                    DurationMilliseconds = 125,
                    Method = "get",
                    Url = "https://example.test/orders?token=secret&view=full",
                    RequestHeaders =
                    [
                        new SessionNetworkHeader { Name = "Authorization", Value = "Bearer secret" }
                    ],
                    RequestBody = new SessionNetworkBody
                    {
                        ContentType = "application/json",
                        Encoding = "utf8",
                        Data = "{\"visible\":\"yes\"}",
                        CapturedBytes = 17,
                        TotalBytes = 17,
                        Truncated = false
                    },
                    StatusCode = 200
                }
            ],
            MetricChannels = [],
            Metrics = []
        };

        store.Save(snapshot);

        var capturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var requestDirectoryPath = Path.Combine(
            capturesRootPath,
            "com.example.network",
            "session-network",
            "network",
            "requests");
        var requestFilePath = Assert.Single(Directory.GetFiles(requestDirectoryPath, "*.json"));
        using (var document = JsonDocument.Parse(File.ReadAllText(requestFilePath)))
        {
            Assert.Equal("ansight.network-request.v1", document.RootElement.GetProperty("schema").GetString());
            Assert.Equal("GET", document.RootElement.GetProperty("method").GetString());
            Assert.Contains("token=%3Credacted%3E", document.RootElement.GetProperty("url").GetString());
            Assert.Equal(
                "<redacted>",
                document.RootElement.GetProperty("requestHeaders")[0].GetProperty("value").GetString());
            Assert.Equal(
                "{\"visible\":\"yes\"}",
                document.RootElement.GetProperty("requestBody").GetProperty("data").GetString());
        }

        Assert.True(store.TryLoad(snapshot.SessionId, out var loaded));
        var request = Assert.Single(loaded!.NetworkRequests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("<redacted>", request.RequestHeaders[0].Value);
        Assert.Equal("{\"visible\":\"yes\"}", request.RequestBody?.Data);
        Assert.Equal(1, loaded.TotalNetworkRequestCount);
        Assert.Equal(1, Assert.Single(store.LoadSummaries()).TotalNetworkRequestCount);
    }
}
