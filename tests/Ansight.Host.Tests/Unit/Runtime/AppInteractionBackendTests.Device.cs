using Ansight.Host.Runtime.Screenshots;
using Ansight.Pairing.Models;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Fact]
    public async Task FreshOcrCaptureDoesNotReuseAnOlderRecordedFrame()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        var path = Path.Combine(environment.RootPath, "fresh-ocr.png");
        using var bitmap = new SKBitmap(30, 60);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = encoded.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var oldFrame = await state.AddSessionEvidenceImageAsync(id, DateTimeOffset.UtcNow.AddMinutes(-1),
            "png", 30, 60, 100, bytes);
        var bridge = new ScreenshotBridge(id, path);
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));

        var evidence = await LiveUiActionEvidence.CaptureScreenshotAsync(
            state, environment.ApplicationPaths, bridge, snapshot!, "scan", "current",
            "simulator-agent-test", default, afterScreenUpdates: true);

        Assert.NotNull(evidence);
        Assert.Equal(1, bridge.Captures);
        Assert.NotEqual(oldFrame!.FrameId, evidence.Frame.FrameId);
        Assert.True(evidence.Frame.CapturedAtUtc > oldFrame.CapturedAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SdkActionEvidenceRequestsAFreshImageWhilePeriodicCaptureIsPaused(bool hostAvailable)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        var path = Path.Combine(environment.RootPath, "fresh.png");
        using var bitmap = new SKBitmap(30, 60);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray());
        var bridge = new ScreenshotBridge(id, path, strictSettling: false);
        var screenshots = new StaticScreenshots { Paused = true, Available = hostAvailable };
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));
        var evidence = await Ansight.Host.Runtime.Operations.Tools.UiAutomation.LiveUiActionEvidence.CaptureScreenshotAsync(
            state, environment.ApplicationPaths, bridge, snapshot!, "action", "after", "simulator-agent-test",
            default, externalScreenshots: screenshots);
        Assert.NotNull(evidence);
        Assert.Equal(1, screenshots.Captures);
        Assert.Equal(hostAvailable ? 0 : 1, bridge.Captures);
        Assert.True(state.TryGetSessionSnapshot(id, out snapshot));
        Assert.Single(snapshot!.Images);
    }

    [Fact]
    public async Task DeviceModeCapturesAndTapsWithDisconnectedSdkAndExportsEvidence()
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget(
            "ios", "simulator-001", "Phone", "test.app", false, false, true)
            { DeviceKind = DeviceKinds.Simulator, ExecutionMode = "device" });
        var bridge = new ScreenshotBridge("unrelated-sdk-session", string.Empty);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        var screenshots = new StaticScreenshots();
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge, externalSessionScreenshotCaptureManager: screenshots);
        var driver = new InputDriver();
        dispatcher.ConfigureUiInputDriver(driver);
        dispatcher.ConfigureUiAccessibilityDriver(driver);
        try
        {
            Assert.False(bridge.IsSessionConnected(id));
            Assert.True(dispatcher.IsSessionConnected(id));
            var requirement = await dispatcher.CallToolAsync("ansight_require_capabilities", new JsonObject
            {
                ["sessionId"] = id, ["capabilities"] = new JsonArray("ui.semantic", "ui.screenshot")
            });
            Assert.False(requirement.Payload?["isError"]?.GetValue<bool>(), requirement.Payload?.ToJsonString());
            var screenshot = await dispatcher.CallToolAsync("ansight_take_screenshot", new JsonObject
            {
                ["sessionId"] = id, ["format"] = "png", ["maxWidth"] = 15
            });
            Assert.False(screenshot.IsError);
            Assert.False(screenshot.Payload?["isError"]?.GetValue<bool>(), screenshot.Payload?.ToJsonString());
            var imageResult = screenshot.Payload?["structuredContent"]?["payload"]?["result"];
            Assert.Equal("png", imageResult?["format"]?.GetValue<string>());
            Assert.Equal(15, imageResult?["width"]?.GetValue<int>());
            using var decoded = SKBitmap.Decode(imageResult?["artifactPath"]?.GetValue<string>());
            Assert.Equal(15, decoded.Width);
            Assert.Equal(30, decoded.Height);
            var repeated = await dispatcher.CallToolAsync("ansight_take_screenshot", new JsonObject { ["sessionId"] = id });
            Assert.False(repeated.Payload?["isError"]?.GetValue<bool>(), repeated.Payload?.ToJsonString());
            Assert.Equal(2, screenshots.Captures);
            var properties = await dispatcher.CallToolAsync("ansight_get_session_properties", new JsonObject { ["sessionId"] = id });
            Assert.Equal("device", properties.Payload?["structuredContent"]?["captureSource"]?.GetValue<string>());
            var overlay = await dispatcher.CallToolAsync("ansight_take_screenshot", new JsonObject
            {
                ["sessionId"] = id, ["annotateNodeIds"] = true
            });
            Assert.Equal("capability_unavailable", overlay.Payload?["structuredContent"]?["code"]?.GetValue<string>());
            var tap = await dispatcher.CallToolAsync("ansight_tap_ui", new JsonObject
            {
                ["sessionId"] = id, ["automationId"] = "search-field"
            });
            Assert.False(tap.IsError);
            Assert.False(tap.Payload?["isError"]?.GetValue<bool>(), tap.Payload?.ToJsonString());
            Assert.Equal(1, driver.Taps);
            Assert.Equal(0, bridge.Captures);
            Assert.Equal(0, bridge.CatalogQueries);
            var missing = await dispatcher.CallToolAsync("ansight_tap_ui", new JsonObject
            {
                ["sessionId"] = id, ["automationId"] = "missing-button", ["visible"] = true
            });
            Assert.True(missing.Payload?["isError"]?.GetValue<bool>());
            var failure = missing.Payload?["structuredContent"];
            Assert.Equal("missing-button", failure?["selector"]?["automationId"]?.GetValue<string>());
            Assert.Contains("missing-button", failure?["message"]?.GetValue<string>());
            Assert.Equal(1, driver.Taps);
            var context = dispatcher.CreateAppInteractionContext(id);
            var observation = await context.ExecuteAsync(new("static-screen", "snapshot"));
            Assert.True(observation.Succeeded);
            Assert.NotNull(observation.Screenshot);
            Assert.True(File.Exists(observation.Screenshot!.ArtifactPath));
        }
        finally
        {
            state.EndDeviceSession(id);
        }

        var archive = new SessionArchiveService(state, state, environment.ApplicationPaths, "node");
        var path = Path.Combine(environment.RootPath, "device.ansight");
        var exported = await archive.ExportSessionArchiveAsync(id, path);
        Assert.True(exported.IsSuccess, exported.Message);
        var imported = await archive.ImportSessionArchiveAsync(path);
        Assert.True(imported.IsSuccess, imported.Message);
        Assert.Equal("device", imported.ImportedSession!.CaptureSource);
        Assert.NotEmpty(imported.ImportedSession.Images);
        Assert.NotEmpty(imported.ImportedSession.VisualTreeSnapshots);
        Assert.False(dispatcher.IsSessionConnected(imported.ImportedSession.SessionId));
    }
    private sealed class StaticScreenshots : IExternalSessionScreenshotCaptureManager
    {
        public bool Paused { get; init; }
        public bool Available { get; init; } = true;
        public int Captures { get; private set; }
        public event EventHandler<ExternalSessionScreenshotCaptureFailedEventArgs>? CaptureFailed { add { } remove { } }
        public Task<byte[]?> CaptureFrameAsync(string sessionId, CancellationToken cancellationToken)
        {
            Captures++;
            if (!Available) return Task.FromResult<byte[]?>(null);
            using var bitmap = new SKBitmap(30, 60);
            bitmap.Erase(SKColors.White);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
        public Task<ExternalSessionScreenshotCapturePolicy> AttachAsync(string sessionId, DeviceAppProfile? profile,
            string? profileJson, ExternalSessionScreenshotCaptureRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IDisposable BeginTestRun(string sessionId) => throw new NotSupportedException();
        public IDisposable SuspendPeriodicCapture(string sessionId) => throw new NotSupportedException();
        public bool IsPeriodicCaptureSuspended(string sessionId) => Paused;
        public event EventHandler<string>? PeriodicCaptureChanged { add { } remove { } }
        public void SetInterval(string sessionId, int intervalMilliseconds) => throw new NotSupportedException();
        public Task StopAsync(string sessionId, string reason) => Task.CompletedTask;
    }
}
