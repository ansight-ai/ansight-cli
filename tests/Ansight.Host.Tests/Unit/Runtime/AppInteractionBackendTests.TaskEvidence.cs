using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;
using Ansight.Infrastructure.Preferences;
using SkiaSharp;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class AppInteractionBackendTests
{
    [Theory]
    [InlineData("ansight_take_screenshot")]
    [InlineData("ansight_capture_screenshot")]
    public async Task ScreenshotCapturePersistsAReusableFrameAfterTemporaryArtifactIsRemoved(string tool)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateSession("test.app", "Test App", IPAddress.Loopback, null, null);
        var path = Path.Combine(environment.RootPath, "transfer.png");
        using var bitmap = new SKBitmap(30, 60);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray());
        var bridge = new ScreenshotBridge(id, path, strictSettling: false);
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), bridge);
        var result = await dispatcher.CallToolAsync(tool, new JsonObject { ["sessionId"] = id, ["format"] = "png" });
        Assert.False(result.Payload?["isError"]?.GetValue<bool>(), result.Payload?.ToJsonString());
        var content = result.Payload!["structuredContent"]!;
        var frameId = content["frameId"]!.GetValue<string>();
        var screenshot = tool == "ansight_take_screenshot" ? content["payload"]!["result"]! : content;
        Assert.NotEqual(path, screenshot["artifactPath"]!.GetValue<string>());
        File.Delete(path);
        var saved = await dispatcher.CallToolAsync("ansight_get_screenshot_frame", new JsonObject
        {
            ["sessionId"] = id, ["frameId"] = frameId
        });
        Assert.False(saved.Payload?["isError"]?.GetValue<bool>(), saved.Payload?.ToJsonString());
        Assert.Equal(frameId, saved.Payload!["structuredContent"]!["frame"]!["frameId"]!.GetValue<string>());
        Assert.True(File.Exists(screenshot["artifactPath"]!.GetValue<string>()));
        Assert.True(state.TryGetSessionSnapshot(id, out var snapshot));
        Assert.Single(snapshot!.Images);

        bitmap.Erase(SKColors.Red);
        using var changed = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await File.WriteAllBytesAsync(path, changed.ToArray());
        var compared = await dispatcher.CallToolAsync("ansight_assert_screenshot", new JsonObject
        {
            ["sessionId"] = id, ["baselineFrameId"] = frameId,
            ["ignoreRegions"] = JsonNode.Parse("""[{"x":0,"y":0,"width":0.5,"height":1}]""")
        });
        var comparison = compared.Payload!["structuredContent"]!;
        Assert.False(comparison["passed"]!.GetValue<bool>());
        Assert.Equal(900, comparison["ignoredPixelCount"]!.GetValue<long>());
        Assert.Equal(900, comparison["totalPixelCount"]!.GetValue<long>());
        Assert.Equal(1d, comparison["differenceRatio"]!.GetValue<double>());
        Assert.Single(comparison["ignoreRegions"]!.AsArray());
        Assert.NotEqual(frameId, comparison["currentFrameId"]!.GetValue<string>());
        File.Delete(path);
        Assert.True(File.Exists(comparison["currentPath"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("fail")]
    [InlineData("cancel")]
    public async Task DirectTasksKeepPeriodicCaptureRunningAndReleaseTheirProfile(string outcome)
    {
        using var environment = new TestEnvironment();
        var state = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var id = state.CreateDeviceSession(new WorkspaceTestTarget("ios", "test-phone", "iPhone", "test.app", false, false, false)
            { DeviceKind = DeviceKinds.Physical, ExecutionMode = "device" });
        var captures = new ExternalSessionScreenshotCaptureManager(state, new UserPreferences(
            new FilePreferencesStore(Path.Combine(environment.RootPath, "preferences.json"))));
        using var bitmap = new SKBitmap(30, 60);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var count = 0;
        captures.ConfigurePhysicalIosCapture((_, _, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(encoded.ToArray());
        });
        var composition = new MefHostComposition(environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));
        using var dispatcher = new OperationDispatcher(state, environment.ApplicationPaths,
            composition.Get<IKnownAppStore>(), composition.Get<IPairingConfigService>(), composition.Get<IPairingConfigCache>(),
            composition.Get<AppService>(), composition.Get<PairingService>(), composition.Get<DotNetProfilingService>(),
            composition.Get<NativeProfilingService>(), new ScreenshotBridge(id, "unused"),
            externalSessionScreenshotCaptureManager: captures);
        dispatcher.ConfigureRepositoryTaskRuntime("node");
        var directory = Path.Combine(environment.RootPath, "ansight", "tasks");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "capture.ts"), $$$"""
            export const task = { "schemaVersion": 1, "appId": "test.app", "title": "Capture continuity",
              "description": "Capture during waits", "timeoutSeconds": 20, "maximumActions": 5 };
            export default async function run({expect}) {
              await new Promise(resolve => setTimeout(resolve, {{{(outcome == "cancel" ? 15000 : 1400)}}}));
              expect({{{(outcome == "fail" ? "false" : "true")}}}, {id: "finished"}).toBe(true);
            }
            """);
        using var cancellation = new CancellationTokenSource();
        try
        {
            Assert.True(state.TryGetSessionSnapshot(id, out var session));
            await captures.AttachAsync(id, session!.DeviceProfile, session.DeviceProfileJson,
                new ExternalSessionScreenshotCaptureRequest(1024, 60_000));
            var run = dispatcher.RunRepositoryTaskAsync(environment.RootPath, "test.app", id, "capture", null, cancellation.Token);
            await TestWait.UntilAsync(() => Volatile.Read(ref count) >= 2 || run.IsCompleted, timeout: TimeSpan.FromSeconds(5));
            Assert.True(Volatile.Read(ref count) >= 2, run.IsCompleted ? (await run).Message : "Capture did not continue.");
            if (outcome == "cancel") cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(outcome switch { "fail" => RepositoryTaskRunStatus.Failed, "cancel" => RepositoryTaskRunStatus.Cancelled,
                _ => RepositoryTaskRunStatus.Passed }, result.Status);
            var completedCount = Volatile.Read(ref count);
            await Task.Delay(850);
            Assert.Equal(completedCount, Volatile.Read(ref count));
            Assert.True(state.IsDeviceSessionActive(id));
        }
        finally { await captures.StopAsync(id, "test completed"); }
    }
}
