using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Devices.Motion;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class DeviceLocationOperationTests
{
    [Fact]
    public async Task Shake_UsesConfiguredMotionDriverAndReturnsDeliveryEvidence()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var driver = new RecordingMotionDriver();
        var router = new DeviceMotionRouter();
        router.Configure(driver);
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(),
            driver: null, motionRouter: router);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_shake_device",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "emulator-5554",
                ["intensity"] = 18,
                ["repetitions"] = 2,
                ["intervalMs"] = 50
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("emulator-5554", driver.DeviceSerial);
        Assert.Equal(4, driver.Samples?.Count);
        Assert.Equal(18, driver.Samples?[0].Acceleration.X);
        Assert.Equal(-18, driver.Samples?[1].Acceleration.X);
        Assert.Equal(200, payload["durationMs"]!.GetValue<int>());
        Assert.Equal("adb-emulator-console", payload["backend"]!.GetValue<string>());
    }

    [Fact]
    public async Task Shake_IosSimulatorPostsOneUIKitGesture()
    {
        const string simulatorUdid = "A1B2C3D4-1111-2222-3333-444455556666";
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var driver = new RecordingMotionDriver();
        var router = new DeviceMotionRouter();
        router.Configure(driver);
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(),
            driver: null, motionRouter: router);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_shake_device",
            ["arguments"] = new JsonObject { ["deviceId"] = simulatorUdid }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal(simulatorUdid, driver.ShakenSimulatorUdid);
        Assert.Null(driver.Samples);
        Assert.Equal("ios", payload["platform"]!.GetValue<string>());
        Assert.Equal("core-simulator-darwin-notification", payload["backend"]!.GetValue<string>());
        Assert.Equal(1, payload["gestureCount"]!.GetValue<int>());
        Assert.Equal(0, payload["sampleCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Shake_IosSimulatorRejectsAndroidPulseOptions()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver: null);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_shake_device",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "A1B2C3D4-1111-2222-3333-444455556666",
                ["intensity"] = 24
            }
        });

        var payload = GetStructuredContent(response, expectedToolError: true);
        Assert.Contains("Android Emulator", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public void ToolCatalog_IncludesMotionTools()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver: null);
        var names = catalog.BuildToolsListResult()["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("ansight_shake_device", names);
        Assert.Contains("ansight_play_accelerometer", names);
    }

    [Theory]
    [InlineData(101, 100)]
    [InlineData(10, 9)]
    public async Task PlayAccelerometer_RejectsInvalidAxisOrHold(double x, int holdMs)
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver: null);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_play_accelerometer",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "emulator-5554",
                ["samples"] = new JsonArray(new JsonObject
                {
                    ["x"] = x, ["y"] = 0, ["z"] = 0, ["holdMs"] = holdMs
                })
            }
        });

        var payload = GetStructuredContent(response, expectedToolError: true);
        Assert.NotNull(payload["message"]);
    }

    private sealed class RecordingMotionDriver : IDeviceMotionDriver
    {
        public string? DeviceSerial { get; private set; }
        public string? ShakenSimulatorUdid { get; private set; }
        public IReadOnlyList<DeviceMotionSample>? Samples { get; private set; }

        public Task ShakeIosSimulatorAsync(string deviceUdid, CancellationToken cancellationToken = default)
        {
            ShakenSimulatorUdid = deviceUdid;
            return Task.CompletedTask;
        }

        public Task PlayMotionAsync(string deviceSerial, IReadOnlyList<DeviceMotionSample> samples,
            CancellationToken cancellationToken = default)
        {
            DeviceSerial = deviceSerial;
            Samples = samples;
            return Task.CompletedTask;
        }
    }
}
