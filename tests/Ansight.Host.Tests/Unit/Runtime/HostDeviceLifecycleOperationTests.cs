using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class HostDeviceLifecycleOperationTests
{
    [Fact]
    public void ResolveNativeDeviceIdentifier_PrefersHostLogDeviceOverAppProfile()
    {
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Example App",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = DateTimeOffset.UtcNow,
            ConfigId = null,
            Status = "Connected",
            LastUpdatedUtc = DateTimeOffset.UtcNow,
            IsHistorical = false,
            DeviceProfileJson = """{"device":{"nativeDeviceId":"Pixel_9a"}}""",
            LogStreams =
            [
                new SessionLogStream
                {
                    StreamId = SessionLogStreamIds.AndroidLogcat,
                    Kind = "native",
                    DisplayName = "Android logcat",
                    Metadata = new Dictionary<string, string>
                    {
                        ["deviceSerial"] = "emulator-5554"
                    }
                }
            ],
            MetricChannels = [],
            Metrics = []
        };

        Assert.Equal(
            "emulator-5554",
            DeviceLifecycleTool.ResolveNativeDeviceIdentifier(snapshot));
    }

    [Fact]
    public async Task LaunchApplication_UsesCapturedSessionAfterAppDisconnects()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new TestAppToolBridge();
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            profile: null,
            profileJson: """{"device":{"nativeDeviceId":"IOS-DEVICE-001"}}""");
        var driver = new FakeLifecycleDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, bridge, driver);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_launch_app",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = sessionId
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("IOS-DEVICE-001", driver.LastLaunchDeviceIdentifier);
        Assert.Equal("com.example.app", driver.LastLaunchBundleIdentifier);
        Assert.Equal("capturedSession", payload["targetSource"]!.GetValue<string>());
        Assert.Equal(sessionId, payload["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task BackgroundApplication_UsesTheEnforcedLiveSessionTarget()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var bridge = new TestAppToolBridge();
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Example App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionDeviceProfile(
            sessionId,
            profile: null,
            profileJson: """{"device":{"nativeDeviceId":"IOS-DEVICE-001"}}""");
        bridge.ConnectedSessionIds.Add(sessionId);
        var driver = new FakeLifecycleDriver();
        var catalog = CreateToolCatalog(runtimeState, environment, bridge, driver);

        var response = await catalog.HandleToolsCallAsync(new JsonObject
        {
            ["name"] = "ansight_background_app",
            ["arguments"] = new JsonObject
            {
                ["sessionId"] = sessionId
            }
        });

        var payload = GetStructuredContent(response);
        Assert.Equal("IOS-DEVICE-001", driver.LastBackgroundDeviceIdentifier);
        Assert.Equal("com.example.app", driver.LastBackgroundBundleIdentifier);
        Assert.Equal("backgroundApplication", payload["operation"]!.GetValue<string>());
        Assert.Equal("liveSession", payload["targetSource"]!.GetValue<string>());
    }

    [Fact]
    public void TypeTextSchema_AdvertisesReplaceExistingDefault()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(
            runtimeState,
            environment,
            new TestAppToolBridge(),
            new FakeLifecycleDriver());

        var typeText = catalog.BuildToolsListResult()["tools"]!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(tool => tool["name"]!.GetValue<string>() == "ansight_type_text");
        var description = typeText["inputSchema"]!["properties"]!["replaceExisting"]!["description"]!
            .GetValue<string>();

        Assert.Contains("Defaults to true", description);
    }

    [Fact]
    public void RepositoryTaskLifecycleSuite_ExposesForegroundAndBackgroundMethods()
    {
        var suite = RepositoryJavaScriptApiMethods.StandardHostToolSuites["lifecycle"];

        Assert.Equal("ansight_foreground_app", suite["foreground"]);
        Assert.Equal("ansight_background_app", suite["background"]);
        Assert.Equal("ansight_launch_app", suite["launch"]);
        Assert.Equal("ansight_terminate_app", suite["terminate"]);
    }

    [Fact]
    public void TypeTextValueReader_PreservesWhitespaceAndAllowsEmptyText()
    {
        Assert.True(LiveUiToolSchemas.TryReadRawString(
            new JsonObject { ["value"] = string.Empty },
            "value",
            out var emptyValue));
        Assert.Equal(string.Empty, emptyValue);

        Assert.True(LiveUiToolSchemas.TryReadRawString(
            new JsonObject { ["value"] = "  exact text  " },
            "value",
            out var spacedValue));
        Assert.Equal("  exact text  ", spacedValue);
    }

    private static ToolCatalog CreateToolCatalog(
        RuntimeState runtimeState,
        TestEnvironment environment,
        TestAppToolBridge appToolBridge,
        IDeviceLifecycleDriver driver)
    {
        var lifecycleRouter = new DeviceLifecycleRouter();
        lifecycleRouter.Configure(driver);
        return new ToolCatalog(
            runtimeState,
            environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths),
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            appToolBridge,
            cloudSessionSharingService: null,
            new SessionResolver(runtimeState, appToolBridge),
            deviceLifecycleRouter: lifecycleRouter);
    }

    private static JsonObject GetStructuredContent(RequestResult response)
    {
        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private sealed class FakeLifecycleDriver : IDeviceLifecycleDriver
    {
        public string? LastLaunchDeviceIdentifier { get; private set; }

        public string? LastLaunchBundleIdentifier { get; private set; }

        public string? LastBackgroundDeviceIdentifier { get; private set; }

        public string? LastBackgroundBundleIdentifier { get; private set; }

        public Task<IReadOnlyList<DeviceLifecycleDevice>> ListDevicesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeviceLifecycleDevice>>([]);

        public Task<DeviceLifecycleResult> StartDeviceAsync(
            string deviceIdentifier,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("startDevice", deviceIdentifier, bundleIdentifier: null));

        public Task<DeviceLifecycleResult> LaunchApplicationAsync(
            string deviceIdentifier,
            string bundleIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastLaunchDeviceIdentifier = deviceIdentifier;
            LastLaunchBundleIdentifier = bundleIdentifier;
            return Task.FromResult(Success("launchApplication", deviceIdentifier, bundleIdentifier));
        }

        public Task<DeviceLifecycleResult> TerminateApplicationAsync(
            string deviceIdentifier,
            string bundleIdentifier,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Success("terminateApplication", deviceIdentifier, bundleIdentifier));

        public Task<DeviceLifecycleResult> BackgroundApplicationAsync(
            string deviceIdentifier,
            string bundleIdentifier,
            CancellationToken cancellationToken = default)
        {
            LastBackgroundDeviceIdentifier = deviceIdentifier;
            LastBackgroundBundleIdentifier = bundleIdentifier;
            return Task.FromResult(Success("backgroundApplication", deviceIdentifier, bundleIdentifier));
        }

        private static DeviceLifecycleResult Success(
            string operation,
            string deviceIdentifier,
            string? bundleIdentifier)
            => new(
                true,
                deviceIdentifier,
                "ios",
                operation,
                bundleIdentifier,
                "Completed.");
    }

    private sealed class TestAppToolBridge : IAppToolBridge
    {
        public HashSet<string> ConnectedSessionIds { get; } = new(StringComparer.Ordinal);

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds() => ConnectedSessionIds.ToArray();

        public bool IsSessionConnected(string sessionId) => ConnectedSessionIds.Contains(sessionId);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure("Not connected.");

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

        public IReadOnlyList<CachedPairingConfig> GetSnapshot() => [];

        public CachedPairingConfig? Find(string configId) => null;

        public bool Remove(string configId) => false;
    }
}
