using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionListingOperationTests
{
    [Fact]
    public async Task GetSessionProperties_ReturnsCustomPropertiesForLiveSession()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession(
            "com.example.properties",
            "Properties App",
            IPAddress.Loopback,
            configId: null,
            processSessionId: null);
        runtimeState.SetSessionCustomProperties(
            sessionId,
            new JsonObject
            {
                ["app"] = new JsonObject
                {
                    ["tenant"] = "acme"
                },
                ["flags"] = new JsonObject
                {
                    ["beta"] = true
                }
            });
        var appToolBridge = new TestAppToolBridge();
        appToolBridge.Connect(sessionId);
        var catalog = CreateToolCatalog(runtimeState, environment, appToolBridge);

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_get_session_properties",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal(sessionId, payload["sessionId"]!.GetValue<string>());
        Assert.Equal("com.example.properties", payload["appId"]!.GetValue<string>());
        Assert.Equal("sdk", payload["captureSource"]!.GetValue<string>());
        Assert.True(payload["isLive"]!.GetValue<bool>());
        Assert.False(payload["isHistorical"]!.GetValue<bool>());
        Assert.Equal("acme", payload["customProperties"]!["app"]!["tenant"]!.GetValue<string>());
        Assert.True(payload["customProperties"]!["flags"]!["beta"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetSessionProperties_ReturnsCustomPropertiesForHistoricalSession()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        ImportSession(runtimeState, CreateSnapshot(
            "session-properties-history-001",
            "com.example.properties",
            "iPhone 17",
            DeviceFormFactors.Phone,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 0,
            isVirtual: false,
            logCount: 0,
            metricCount: 0,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:03:00Z"),
            customProperties: new JsonObject
            {
                ["release"] = new JsonObject
                {
                    ["channel"] = "beta"
                }
            }));
        var catalog = CreateToolCatalog(runtimeState, environment);

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_get_session_properties",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = "session-properties-history-001"
                }
            });

        var payload = GetStructuredContent(response);
        Assert.False(payload["isLive"]!.GetValue<bool>());
        Assert.True(payload["isHistorical"]!.GetValue<bool>());
        Assert.Equal("beta", payload["customProperties"]!["release"]!["channel"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListSessions_FiltersByDeviceProfileBeforeLimit()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        ImportSession(runtimeState, CreateSnapshot(
            "session-ios-phone-001",
            "com.example.ios",
            "iPhone 15",
            DeviceFormFactors.Phone,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 0,
            isVirtual: false,
            logCount: 1,
            metricCount: 1,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:00:00Z")));
        ImportSession(runtimeState, CreateSnapshot(
            "session-ios-tablet-sim-001",
            "com.example.ios",
            "iPad",
            DeviceFormFactors.Tablet,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 1,
            isVirtual: true,
            logCount: 1,
            metricCount: 0,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:01:00Z")));
        ImportSession(runtimeState, CreateSnapshot(
            "session-android-phone-001",
            "com.example.android",
            "Pixel 9",
            DeviceFormFactors.Phone,
            osName: "android",
            osVersion: "16",
            deviceClassCode: 0,
            isVirtual: false,
            logCount: 1,
            metricCount: 1,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:02:00Z")));

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_list_sessions",
                ["arguments"] = new JsonObject
                {
                    ["platform"] = "apple",
                    ["idiom"] = "tablet",
                    ["deviceType"] = "sim",
                    ["osVersion"] = "26.4",
                    ["deviceClassCode"] = 1,
                    ["limit"] = 1
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal(1, payload["matchedCount"]!.GetValue<int>());
        Assert.False(payload["isTruncated"]!.GetValue<bool>());
        var session = Assert.Single(payload["sessions"]!.AsArray());
        Assert.Equal("session-ios-tablet-sim-001", session!["sessionId"]!.GetValue<string>());
        Assert.Equal("tablet", session["deviceFormFactor"]!.GetValue<string>());
        Assert.True(session["isVirtual"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ListSessions_FiltersLogsAndTelemetryBeforeLimit()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        ImportSession(runtimeState, CreateSnapshot(
            "session-newer-no-telemetry-001",
            "com.example.telemetry",
            "iPad",
            DeviceFormFactors.Tablet,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 1,
            isVirtual: true,
            logCount: 1,
            metricCount: 0,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:02:00Z")));
        ImportSession(runtimeState, CreateSnapshot(
            "session-older-with-telemetry-001",
            "com.example.telemetry",
            "iPhone 15",
            DeviceFormFactors.Phone,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 0,
            isVirtual: false,
            logCount: 1,
            metricCount: 1,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:01:00Z")));

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_list_sessions",
                ["arguments"] = new JsonObject
                {
                    ["hasTelemetry"] = true,
                    ["limit"] = 1
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal(1, payload["matchedCount"]!.GetValue<int>());
        var session = Assert.Single(payload["sessions"]!.AsArray());
        Assert.Equal("session-older-with-telemetry-001", session!["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListDevices_FiltersByPhysicalPlatformAndFormFactor()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        ImportSession(runtimeState, CreateSnapshot(
            "session-ios-tablet-sim-001",
            "com.example.ios",
            "iPad",
            DeviceFormFactors.Tablet,
            osName: "iOS",
            osVersion: "26.4",
            deviceClassCode: 1,
            isVirtual: true,
            logCount: 1,
            metricCount: 0,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:01:00Z")));
        ImportSession(runtimeState, CreateSnapshot(
            "session-android-phone-001",
            "com.example.android",
            "Pixel 9",
            DeviceFormFactors.Phone,
            osName: "android",
            osVersion: "16",
            deviceClassCode: 0,
            isVirtual: false,
            logCount: 0,
            metricCount: 0,
            lastUpdatedUtc: DateTimeOffset.Parse("2026-04-01T05:02:00Z")));

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_list_devices",
                ["arguments"] = new JsonObject
                {
                    ["includeHistorical"] = true,
                    ["platforms"] = new JsonArray("android"),
                    ["deviceFormFactors"] = new JsonArray("phone"),
                    ["deviceType"] = "physical"
                }
            });

        var payload = GetStructuredContent(response);
        var device = Assert.Single(payload["devices"]!.AsArray());
        Assert.Equal("session-android-phone-001", device!["sessionId"]!.GetValue<string>());
        Assert.Equal("android", device["device"]!["osName"]!.GetValue<string>());
        Assert.False(device["device"]!["isVirtual"]!.GetValue<bool>());
    }

    private static ToolCatalog CreateToolCatalog(
        RuntimeState runtimeState,
        TestEnvironment environment,
        TestAppToolBridge? appToolBridge = null)
    {
        appToolBridge ??= new TestAppToolBridge();
        return new ToolCatalog(
            runtimeState,
            environment.ApplicationPaths,
            new KnownAppStore(environment.ApplicationPaths),
            new EmptyPairingConfigService(),
            new EmptyPairingConfigCache(),
            appToolBridge,
            cloudSessionSharingService: null,
            new SessionResolver(runtimeState, appToolBridge));
    }

    private static void ImportSession(RuntimeState runtimeState, AppSessionSnapshot snapshot)
    {
        var importResult = runtimeState.ImportSessionSnapshot(snapshot, new Dictionary<string, byte[]>());
        Assert.True(importResult.IsSuccess);
    }

    private static AppSessionSnapshot CreateSnapshot(
        string sessionId,
        string appId,
        string deviceModel,
        string formFactor,
        string osName,
        string osVersion,
        int deviceClassCode,
        bool isVirtual,
        int logCount,
        int metricCount,
        DateTimeOffset lastUpdatedUtc,
        JsonObject? customProperties = null)
    {
        var createdUtc = lastUpdatedUtc.AddMinutes(-5);
        return new AppSessionSnapshot
        {
            SessionId = sessionId,
            AppId = appId,
            ClientName = "Operation Listing Test Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = null,
            Status = "WebSocket Closed",
            LastUpdatedUtc = lastUpdatedUtc,
            IsHistorical = true,
            CustomProperties = customProperties,
            DeviceProfile = new DeviceAppProfile
            {
                Device = new DeviceProfile
                {
                    Model = deviceModel,
                    FormFactor = formFactor,
                    OsName = osName,
                    OsVersion = osVersion,
                    DeviceClassCode = deviceClassCode,
                    IsVirtual = isVirtual,
                    IsEmulator = isVirtual
                },
                App = new DeviceApplicationProfile
                {
                    AppId = appId,
                    AppName = appId,
                    VersionName = "1.0"
                }
            },
            Logs = Enumerable.Range(0, logCount)
                .Select(index => new LogEntry(createdUtc.AddSeconds(index), $"Log {index}")
                {
                    EventId = $"event-{index:D3}"
                })
                .ToArray(),
            MetricChannels = metricCount > 0
                ? [new SessionMetricChannel { ChannelId = 1, Name = "FPS", ColorHex = "#00FF00" }]
                : [],
            Metrics = Enumerable.Range(0, metricCount)
                .Select(index => new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = 60 + index,
                    CapturedAtUtc = createdUtc.AddSeconds(index),
                    SegmentId = 1
                })
                .ToArray()
        };
    }

    private static JsonObject GetStructuredContent(RequestResult response)
    {
        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private sealed class TestAppToolBridge : IAppToolBridge
    {
        private readonly HashSet<string> connectedSessionIds = new(StringComparer.Ordinal);

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> GetConnectedSessionIds()
            => connectedSessionIds.ToArray();

        public bool IsSessionConnected(string sessionId)
            => connectedSessionIds.Contains(sessionId);

        public void Connect(string sessionId)
            => connectedSessionIds.Add(sessionId);

        public OperationResult ForceDisconnectSession(string sessionId)
            => OperationResult.Failure($"Session '{sessionId}' is not connected.");

        public Task<AppToolBridgeResponse> QueryToolsAsync(
            string sessionId,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            return Task.FromResult(AppToolBridgeResponse.FromFailure("No connected sessions."));
        }

        public Task<AppToolBridgeResponse> CallToolAsync(
            string sessionId,
            string toolId,
            JsonObject? arguments,
            CancellationToken cancellationToken,
            AppToolBridgeRequestContext? requestContext = null)
        {
            return Task.FromResult(AppToolBridgeResponse.FromFailure("No connected sessions."));
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
