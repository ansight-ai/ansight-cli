using System.Net;
using System.Text;
using System.Text.Json;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class IosPhysicalDeviceAppiumClientTests
{
    [Fact]
    public async Task MonitoringSharesOneSessionPerDeviceEvenDuringConcurrentDiscovery()
    {
        var handler = new RecordingAppiumHandler();
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        await Task.WhenAll(
            client.PrepareMonitoringAsync("physical-udid", "com.example.first", CancellationToken.None),
            client.PrepareMonitoringAsync("physical-udid", "com.example.second", CancellationToken.None));
        Assert.Equal(1, handler.SessionRequests);
        Assert.Equal(client.GetSessionId("physical-udid", "com.example.first"),
            client.GetSessionId("physical-udid", "com.example.second"));
        await client.PrepareMonitoringAsync("another-iphone", "com.example.second", CancellationToken.None);
        Assert.Equal(2, handler.SessionRequests);
    }

    [Theory]
    [InlineData("/execute/sync")]
    [InlineData("/screenshot")]
    [InlineData("/source")]
    public async Task MonitoringReadReconnectsOnceAfterAnExpiredSessionWithoutLaunching(string suffix)
    {
        var handler = new RecordingAppiumHandler { FailedPathSuffix = suffix, FailuresRemaining = 1 };
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        await client.PrepareMonitoringAsync("physical-udid", "com.example.target", CancellationToken.None);
        switch (suffix)
        {
            case "/execute/sync":
                Assert.True(await client.IsApplicationForegroundAsync("physical-udid", "com.example.target", CancellationToken.None));
                break;
            case "/screenshot":
                Assert.Equal([1, 2, 3], await client.GetScreenshotAsync("physical-udid", "com.example.target", CancellationToken.None));
                break;
            default:
                Assert.Equal("<AppiumAUT />", (await client.GetPageSourceAsync("physical-udid", "com.example.target", CancellationToken.None)).Source);
                break;
        }
        Assert.Equal(2, handler.SessionRequests);
        Assert.Equal("appium-session-2", client.GetSessionId("physical-udid", "com.example.target"));
        Assert.Contains($"/session/appium-session-2{suffix}", handler.Paths);
        using var request = JsonDocument.Parse(handler.SessionRequestBody!);
        Assert.False(request.RootElement.GetProperty("capabilities").GetProperty("alwaysMatch")
            .GetProperty("appium:autoLaunch").GetBoolean());
    }

    [Fact]
    public async Task RepeatedSessionLossStopsAfterOneRetryAndReportsTheAppiumError()
    {
        var handler = new RecordingAppiumHandler { FailedPathSuffix = "/execute/sync", FailuresRemaining = 2 };
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.IsApplicationForegroundAsync(
            "physical-udid", "com.example.target", CancellationToken.None));
        Assert.Contains("Session expired", error.Message);
        Assert.Equal(2, handler.SessionRequests);
        Assert.Null(client.GetSessionId("physical-udid", "com.example.target"));
    }

    [Fact]
    public async Task OtherWebDriverErrorsDoNotRecreateTheConnection()
    {
        var handler = new RecordingAppiumHandler
        {
            FailedPathSuffix = "/source", FailuresRemaining = 1, FailureError = "no such element"
        };
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetPageSourceAsync(
            "physical-udid", "com.example.target", CancellationToken.None));
        Assert.Equal(1, handler.SessionRequests);
        Assert.Equal("appium-session", client.GetSessionId("physical-udid", "com.example.target"));
    }

    [Fact]
    public async Task ExpiredSessionDoesNotReplayInput()
    {
        var handler = new RecordingAppiumHandler { FailedPathSuffix = "/actions", FailuresRemaining = 1 };
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.TapAsync(
            "physical-udid", "com.example.target", 0.5, 0.5, CancellationToken.None));
        Assert.Equal(1, handler.SessionRequests);
        Assert.Single(handler.Paths, path => path.EndsWith("/actions", StringComparison.Ordinal));
        Assert.Null(client.GetSessionId("physical-udid", "com.example.target"));
    }

    [Fact]
    public async Task ReleasingAMonitorClearsTheSharedConnectionForTheNextApp()
    {
        var handler = new RecordingAppiumHandler();
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);
        await client.PrepareMonitoringAsync("physical-udid", "com.example.first", CancellationToken.None);
        await client.PrepareMonitoringAsync("physical-udid", "com.example.second", CancellationToken.None);
        await client.ReleaseSessionAsync("physical-udid", "com.example.second", CancellationToken.None);
        Assert.Null(client.GetSessionId("physical-udid", "com.example.first"));
        await client.PrepareMonitoringAsync("physical-udid", "com.example.first", CancellationToken.None);
        Assert.Equal(2, handler.SessionRequests);
    }

    [Fact]
    public async Task TapAsync_CreatesSignedXcuiTestSessionForExactDeviceAndApp()
    {
        var handler = new RecordingAppiumHandler();
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(
                "http://127.0.0.1:4723/wd/hub",
                "TEAM12345",
                "Apple Development",
                "com.example.AnsightWebDriverAgent",
                "/tmp/ansight-wda.xcconfig"),
            handler);

        await client.TapAsync(
            "00008140-0011223344556677",
            "com.example.target",
            0.25,
            0.75,
            CancellationToken.None);

        Assert.Equal(
            [
                "/wd/hub/session",
                "/wd/hub/session/appium-session/window/rect",
                "/wd/hub/session/appium-session/actions"
            ],
            handler.Paths);
        using var request = JsonDocument.Parse(handler.SessionRequestBody!);
        var capabilities = request.RootElement
            .GetProperty("capabilities")
            .GetProperty("alwaysMatch");
        Assert.Equal("XCUITest", capabilities.GetProperty("appium:automationName").GetString());
        Assert.Equal("00008140-0011223344556677", capabilities.GetProperty("appium:udid").GetString());
        Assert.Equal("com.example.target", capabilities.GetProperty("appium:bundleId").GetString());
        Assert.Equal("TEAM12345", capabilities.GetProperty("appium:xcodeOrgId").GetString());
        Assert.Equal("Apple Development", capabilities.GetProperty("appium:xcodeSigningId").GetString());
        Assert.Equal(
            "com.example.AnsightWebDriverAgent",
            capabilities.GetProperty("appium:updatedWDABundleId").GetString());
        Assert.Equal(
            "/tmp/ansight-wda.xcconfig",
            capabilities.GetProperty("appium:xcodeConfigFile").GetString());
        Assert.Equal(
            "appium-session",
            client.GetSessionId("00008140-0011223344556677", "com.example.target"));
    }

    [Fact]
    public async Task GetPageSourceAsync_ReadsWebDriverAgentAccessibilitySource()
    {
        var handler = new RecordingAppiumHandler();
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null),
            handler);

        var source = await client.GetPageSourceAsync(
            "simulator-udid",
            "com.example.target",
            CancellationToken.None);

        Assert.Equal("<AppiumAUT />", source.Source);
        Assert.Equal(400, source.ViewportWidth);
        Assert.Equal(800, source.ViewportHeight);
        Assert.Equal(
            ["/session", "/session/appium-session/window/rect", "/session/appium-session/source"],
            handler.Paths);
    }

    [Fact]
    public async Task MonitoringDoesNotLaunchTheAppAndCapturesScreenshots()
    {
        var handler = new RecordingAppiumHandler();
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);

        await client.PrepareMonitoringAsync("physical-udid", "com.example.target", CancellationToken.None);
        var screenshot = await client.GetScreenshotAsync("physical-udid", "com.example.target", CancellationToken.None);

        Assert.Equal([1, 2, 3], screenshot);
        using var request = JsonDocument.Parse(handler.SessionRequestBody!);
        Assert.False(request.RootElement.GetProperty("capabilities").GetProperty("alwaysMatch")
            .GetProperty("appium:autoLaunch").GetBoolean());
        Assert.Equal(["/session", "/session/appium-session/window/rect", "/session/appium-session/screenshot"], handler.Paths);
    }

    [Fact]
    public async Task ForegroundCheckUsesActiveAppInfoWithoutLaunchingTheTarget()
    {
        var handler = new RecordingAppiumHandler { ActiveBundleId = "com.apple.springboard" };
        using var client = new IosPhysicalDeviceAppiumClient(
            new IosPhysicalDeviceAppiumOptions(null, null, null, null, null), handler);

        Assert.False(await client.IsApplicationForegroundAsync("physical-udid", "com.example.target", CancellationToken.None));
        handler.ActiveBundleId = "com.example.target";
        Assert.True(await client.IsApplicationForegroundAsync("physical-udid", "com.example.target", CancellationToken.None));
        using var request = JsonDocument.Parse(handler.SessionRequestBody!);
        Assert.False(request.RootElement.GetProperty("capabilities").GetProperty("alwaysMatch")
            .GetProperty("appium:autoLaunch").GetBoolean());
        Assert.Equal(
            ["/session", "/session/appium-session/window/rect", "/session/appium-session/execute/sync",
                "/session/appium-session/execute/sync"], handler.Paths);
    }

    private sealed class RecordingAppiumHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        public string? SessionRequestBody { get; private set; }

        public string ActiveBundleId { get; set; } = "com.example.target";
        public int SessionRequests { get; private set; }
        public string? FailedPathSuffix { get; init; }
        public string FailureError { get; init; } = "invalid session id";
        public int FailuresRemaining { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            if (request.Method == HttpMethod.Post && path.EndsWith("/session", StringComparison.Ordinal))
            {
                await Task.Yield();
                SessionRequests++;
                SessionRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    value = new { sessionId = SessionRequests == 1 ? "appium-session" : $"appium-session-{SessionRequests}" }
                }));
            }

            if (FailedPathSuffix is not null && path.EndsWith(FailedPathSuffix, StringComparison.Ordinal)
                && FailuresRemaining > 0)
            {
                FailuresRemaining--;
                var response = JsonResponse(JsonSerializer.Serialize(new
                {
                    value = new { error = FailureError, message = "Session expired" }
                }));
                response.StatusCode = HttpStatusCode.NotFound;
                return response;
            }

            if (path.EndsWith("/window/rect", StringComparison.Ordinal))
            {
                return JsonResponse("""{"value":{"width":400,"height":800}}""");
            }

            if (path.EndsWith("/source", StringComparison.Ordinal))
            {
                return JsonResponse("""{"value":"<AppiumAUT />"}""");
            }

            if (path.EndsWith("/screenshot", StringComparison.Ordinal))
            {
                return JsonResponse("{\"value\":\"AQID\"}");
            }

            if (path.EndsWith("/execute/sync", StringComparison.Ordinal))
            {
                return JsonResponse(JsonSerializer.Serialize(new { value = new { bundleId = ActiveBundleId } }));
            }

            return JsonResponse("""{"value":null}""");
        }

        private static HttpResponseMessage JsonResponse(string content)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
    }
}
