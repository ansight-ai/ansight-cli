using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class DeviceLocationOperationTests
{
    [Fact]
    public void ToolCatalog_IncludesDeviceLocationTools()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), new FakeLocationDriver());

        var tools = catalog.BuildToolsListResult()["tools"]!.AsArray();
        var toolNames = tools.Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

        Assert.Contains("ansight_play_device_location", toolNames);
        Assert.Contains("ansight_set_device_location", toolNames);
        Assert.Contains("ansight_clear_device_location", toolNames);
        var setTool = tools
            .Select(tool => tool!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "ansight_set_device_location");
        var required = setTool["inputSchema"]!["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();
        Assert.Contains("latitude", required);
        Assert.Contains("longitude", required);
    }

    [Fact]
    public async Task SetDeviceLocation_TargetsExplicitDevice()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var driver = new FakeLocationDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_set_device_location",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "IOS-DEVICE-001",
                ["latitude"] = -33.8688,
                ["longitude"] = 151.2093
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("IOS-DEVICE-001", driver.LastSetRequest?.DeviceIdentifier);
        Assert.Equal(-33.8688, driver.LastSetRequest?.Latitude);
        Assert.Equal(151.2093, driver.LastSetRequest?.Longitude);
        Assert.Equal("deviceId", payload["targetSource"]!.GetValue<string>());
        Assert.Equal("simctl", payload["backend"]!.GetValue<string>());
        Assert.Equal("iPhone 17 Pro", payload["deviceName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("latitude", -91)]
    [InlineData("latitude", 91)]
    [InlineData("longitude", -181)]
    [InlineData("longitude", 181)]
    public async Task SetDeviceLocation_RejectsOutOfRangeCoordinates(string propertyName, double invalidValue)
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var driver = new FakeLocationDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver);
        var arguments = new JsonObject
        {
            ["latitude"] = -33.8688,
            ["longitude"] = 151.2093
        };
        arguments[propertyName] = invalidValue;

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_set_device_location",
            ["arguments"] = arguments
        });

        var payload = GetStructuredContent(response, expectedToolError: true);
        Assert.Contains("must be between", payload["message"]!.GetValue<string>());
        Assert.Null(driver.LastSetRequest);
    }

    [Fact]
    public async Task SetDeviceLocation_ResolvesNativeDeviceFromLiveSession()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new TestAppToolBridge();
        var sessionId = runtimeState.CreateSession(
            "com.example.location",
            "Location App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            profile: null,
            profileJson: """{"device":{"nativeDeviceId":"emulator-5554"}}""");
        bridge.Connect(sessionId);
        var driver = new FakeLocationDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, bridge, driver);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_set_device_location",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = sessionId,
                ["latitude"] = 37.7749,
                ["longitude"] = -122.4194
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("emulator-5554", driver.LastSetRequest?.DeviceIdentifier);
        Assert.Equal(sessionId, payload["sessionId"]!.GetValue<string>());
        Assert.Equal("com.example.location", payload["appId"]!.GetValue<string>());
        Assert.Equal("liveSession", payload["targetSource"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("ansight_set_device_location")]
    [InlineData("ansight_clear_device_location")]
    public async Task DeviceLocation_PreservesLegacyTargetSourceAlongsideCanonicalSourceWhenTargetIsOmitted(string toolName)
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var driver = new FakeLocationDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = toolName == "ansight_set_device_location"
                ? new JsonObject { ["latitude"] = 37.7749, ["longitude"] = -122.4194 }
                : new JsonObject()
        });

        var payload = GetStructuredContent(response);
        if (toolName == "ansight_set_device_location")
        {
            Assert.NotNull(driver.LastSetRequest);
            Assert.Null(driver.LastSetRequest!.DeviceIdentifier);
        }
        else
        {
            Assert.NotNull(driver.LastClearRequest);
            Assert.Null(driver.LastClearRequest!.DeviceIdentifier);
        }
        Assert.Equal("hostSelection", payload["targetSource"]!.GetValue<string>());
    }

    [Fact]
    public async Task SetDeviceLocation_WithoutDriverReturnsSupportedCliGuidance()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment, new TestAppToolBridge(), driver: null);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_set_device_location",
            ["arguments"] = new JsonObject
            {
                ["deviceId"] = "emulator-5554",
                ["latitude"] = 37.7749,
                ["longitude"] = -122.4194
            }
        });

        var payload = GetStructuredContent(response, expectedToolError: true);
        Assert.False(payload["isSuccess"]!.GetValue<bool>());
        Assert.Equal("deviceId", payload["targetSource"]!.GetValue<string>());
        Assert.Contains("ansight device location", payload["message"]!.GetValue<string>());
    }

    private static ToolCatalog CreateToolCatalog(
        RuntimeState runtimeState,
        TestEnvironment environment,
        TestAppToolBridge appToolBridge,
        IDeviceLocationDriver? driver,
        DeviceLocationPlaybackService? playback = null,
        IDeviceService? devices = null,
        DeviceMotionRouter? motionRouter = null)
    {
        var locationRouter = new DeviceLocationRouter();
        locationRouter.Configure(driver);
        if (playback is not null)
            locationRouter.ConfigurePlayback(playback, devices!);
        return new ToolCatalog(
            runtimeState,
            environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths),
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            appToolBridge,
            cloudSessionSharingService: null,
            new SessionResolver(runtimeState, appToolBridge),
            deviceLocationRouter: locationRouter,
            deviceMotionRouter: motionRouter);
    }

    private static JsonObject GetStructuredContent(RequestResult response, bool expectedToolError = false)
    {
        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.True(expectedToolError == response.Payload!["isError"]!.GetValue<bool>(), response.Payload.ToJsonString());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private sealed class FakeLocationDriver : IDeviceLocationDriver
    {
        public SetDeviceLocationRequest? LastSetRequest { get; private set; }

        public ClearDeviceLocationRequest? LastClearRequest { get; private set; }

        public Task<DeviceLocationResult> SetLocationAsync(
            SetDeviceLocationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSetRequest = request;
            return Task.FromResult(new DeviceLocationResult(
                true,
                "simctl",
                "Location set.",
                request.DeviceIdentifier ?? "selected-device",
                "iPhone 17 Pro",
                "ios"));
        }

        public Task<DeviceLocationResult> ClearLocationAsync(
            ClearDeviceLocationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastClearRequest = request;
            return Task.FromResult(new DeviceLocationResult(
                true,
                "simctl",
                "Location cleared.",
                request.DeviceIdentifier ?? "selected-device",
                "iPhone 17 Pro",
                "ios"));
        }
    }

    private sealed class TestAppToolBridge : IAppToolBridge
    {
        private readonly HashSet<string> connectedSessionIds = new(StringComparer.Ordinal);

        public event EventHandler? ConnectionsChanged;

        public IReadOnlyList<string> GetConnectedSessionIds()
            => connectedSessionIds.ToArray();

        public bool IsSessionConnected(string sessionId)
            => connectedSessionIds.Contains(sessionId);

        public OperationResult ForceDisconnectSession(string sessionId)
            => connectedSessionIds.Remove(sessionId)
                ? OperationResult.Success($"Disconnected session '{sessionId}'.")
                : OperationResult.Failure($"Session '{sessionId}' is not connected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No remote tools."));

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
            => Task.FromResult(AppToolBridgeResponse.FromFailure("No remote tools."));

        public void Connect(string sessionId)
        {
            connectedSessionIds.Add(sessionId);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class EmptyPairingConfigService : IPairingConfigService
    {
        public PairingIssueResult Issue(string appName, string appId, PairingConfigDuration duration)
            => throw new NotSupportedException();
    }

    private sealed class EmptyPairingConfigCache : IPairingConfigCache
    {
        public void Add(PairingConfig config)
        {
        }

        public void Add(CachedPairingConfig config)
        {
        }

        public IReadOnlyList<CachedPairingConfig> GetSnapshot()
            => Array.Empty<CachedPairingConfig>();

        public CachedPairingConfig? Find(string configId)
            => null;

        public bool Remove(string configId)
            => false;
    }
}
