using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class ExplorerAppWatchTests
{
    [Fact]
    public async Task ExplorerManagesPersistentAppWatchesWithoutADeviceRegistration()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = Assert.IsType<Uri>(explorer.ExplorerUrl) };
        using var added = await client.PostAsJsonAsync("api/app-watches/add", new
        {
            appId = "com.example.notes", captureFiles = new[] { "Documents/notes.sqlite" },
            screenshotIntervalMilliseconds = 1000
        });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var result = JsonNode.Parse(await added.Content.ReadAsStringAsync())!;
        var watch = Assert.Single(result["watches"]!.AsArray())!["watch"]!;
        var id = watch["id"]!.GetValue<string>();
        Assert.Null(watch["deviceId"]);
        Assert.Null(watch["platform"]);
        Assert.True(watch["enabled"]!.GetValue<bool>());
        Assert.Equal("Documents/notes.sqlite", watch["captureFiles"]![0]!.GetValue<string>());
        Assert.Equal(1000, watch["screenshotIntervalMilliseconds"]!.GetValue<int>());

        foreach (var action in new[] { "disable", "enable" })
        {
            using var updated = await client.PostAsJsonAsync($"api/app-watches/{id}/{action}", new { });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var list = JsonNode.Parse(await client.GetStringAsync("api/app-watches"))!;
            Assert.Equal(action == "enable", Assert.Single(list["watches"]!.AsArray())!["watch"]!["enabled"]!.GetValue<bool>());
            var persisted = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(runtime.ApplicationPaths.ApplicationDataPath, "app-watches.json")))!;
            Assert.Equal(action == "enable", Assert.Single(persisted.AsArray())!["enabled"]!.GetValue<bool>());
        }

        using var configured = await client.PostAsJsonAsync($"api/app-watches/{id}/configure", new
        {
            screenshotIntervalMilliseconds = 500
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        var saved = Assert.Single(runtime.AppWatches.List()).Watch;
        Assert.Equal(500, saved.ScreenshotIntervalMilliseconds);
        Assert.Equal("Documents/notes.sqlite", Assert.Single(saved.CaptureFiles));
        var configuredFile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            runtime.ApplicationPaths.ApplicationDataPath, "app-watches.json")))!;
        Assert.Equal(500, Assert.Single(configuredFile.AsArray())!["screenshotIntervalMilliseconds"]!.GetValue<int>());
        foreach (var body in new object[] { new { }, new { screenshotIntervalMilliseconds = 0 },
                     new { screenshotIntervalMilliseconds = 60001 } })
        {
            using var rejected = await client.PostAsJsonAsync($"api/app-watches/{id}/configure", body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(500, Assert.Single(runtime.AppWatches.List()).Watch.ScreenshotIntervalMilliseconds);
        }

        using var removed = await client.PostAsJsonAsync($"api/app-watches/{id}/remove", new { });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(runtime.AppWatches.List());
        using var unknown = await client.PostAsJsonAsync($"api/app-watches/{id}/enable", new { });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Theory]
    [InlineData("{\"appId\":\"\"}")]
    [InlineData("{\"appId\":\"com.example.notes\",\"platform\":\"macos\"}")]
    [InlineData("{\"appId\":\"com.example.notes\",\"captureFiles\":[\"../secret\"]}")]
    [InlineData("{\"appId\":\"com.example.notes\",\"captureFiles\":null}")]
    [InlineData("{\"appId\":\"com.example.notes\",\"screenshotIntervalMilliseconds\":0}")]
    [InlineData("{\"appId\":\"com.example.notes\",\"screenshotIntervalMilliseconds\":60001}")]
    public async Task InvalidMonitoringRequestsDoNotCreateWatches(string body)
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        using var client = new HttpClient { BaseAddress = Assert.IsType<Uri>(explorer.ExplorerUrl) };
        using var response = await client.PostAsync("api/app-watches/add", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(runtime.AppWatches.List());
    }
}
