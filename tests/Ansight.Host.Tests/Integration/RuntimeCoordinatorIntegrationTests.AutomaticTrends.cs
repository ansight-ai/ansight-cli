using Ansight.Host.Trends;
using Ansight.Host.Tests.TestSupport;
using System.Text.Json.Serialization;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Fact(Timeout = 60000)]
    public async Task FinalizedRegisteredAppSession_AutomaticallyEvaluatesTrends()
    {
        const string appId = "com.example.automatic-trends";
        using var environment = new TestEnvironment();
        var pairing = environment.SeedPairingConfig(appId, "Automatic Trends App");
        var workspacePath = Path.Combine(environment.RootPath, "workspace");
        WriteAutomaticTrendsWorkspace(workspacePath, appId);
        using var runtime = environment.CreateRuntime();
        var registration = runtime.Apps.Register(new AppRegistrationRequest(
            appId,
            "Automatic Trends App",
            workspacePath));
        Assert.True(registration.IsSuccess, registration.Message);
        Assert.True(registration.App?.AutomaticTrendsMonitoringEnabled);
        var captureEvents = new ConcurrentQueue<RuntimeSessionCaptureEvent>();
        runtime.SessionCaptureEventOccurred += (_, runtimeEvent) => captureEvents.Enqueue(runtimeEvent);
        await runtime.StartAsync();

        var sessionId = await StreamTrendsSessionAsync(
            runtime,
            pairing,
            captureEvents,
            "1.0.0",
            60);
        var report = await WaitForAutomaticTrendsReportAsync(runtime, appId, sessionId);

        Assert.Equal("1.0.0", report.Context.AppVersion);
        Assert.Equal(WorkspaceTrendsStatus.Passed, report.Status);
        var observedCheck = Assert.Single(report.Checks, check => check.TrendsId == "checkout-fps");
        Assert.Equal(60d, Assert.Single(observedCheck.Metrics).Value);
        Assert.Single(report.History);
        Assert.Contains(captureEvents, runtimeEvent =>
            runtimeEvent.SessionId == sessionId
            && runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Finalized);
    }

    private static async Task<string> StreamTrendsSessionAsync(
        RuntimeCoordinator runtime,
        SeededPairingConfig pairingConfig,
        ConcurrentQueue<RuntimeSessionCaptureEvent> captureEvents,
        string appVersion,
        long framesPerSecond)
    {
        var previousSessionIds = runtime.Sessions.GetSummaries()
            .Select(static session => session.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var connectResponse = await SendConnectRequestAsync(pairingConfig);
        Assert.True(connectResponse.Accepted, connectResponse.ReasonMessage ?? connectResponse.Reason);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new Uri(
                $"ws://127.0.0.1:{connectResponse.WebSocketPort}{connectResponse.WebSocketPath}?token={connectResponse.WebSocketToken}"),
            CancellationToken.None);

        string? sessionId = null;
        await TestWait.UntilAsync(
            () =>
            {
                sessionId = captureEvents
                    .LastOrDefault(runtimeEvent =>
                        runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Started
                        && string.Equals(runtimeEvent.AppId, pairingConfig.AppId, StringComparison.Ordinal)
                        && !previousSessionIds.Contains(runtimeEvent.SessionId))
                    ?.SessionId;
                return sessionId is not null;
            },
            because: "The new automatic-trends session should start capture.");

        var spanStartedUtc = DateTimeOffset.UtcNow;
        var spanCompletedUtc = spanStartedUtc.AddSeconds(1);
        await SendJsonAsync(
            socket,
            JsonSerializer.SerializeToNode(
                new DeviceAppProfile
                {
                    Sdk = new DeviceSdkProfile
                    {
                        Name = "Ansight .NET SDK",
                        Version = "0.1.0-test",
                        Language = "dotnet"
                    },
                    Device = new DeviceProfile
                    {
                        Model = "iPhone 15",
                        OsName = "iOS",
                        OsVersion = "18.0",
                        IsEmulator = true
                    },
                    App = new DeviceApplicationProfile
                    {
                        AppId = pairingConfig.AppId,
                        AppName = pairingConfig.AppName,
                        VersionName = appVersion
                    }
                },
                protocolJson)!.AsObject());
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_METRIC_CHANNELS",
            ["channels"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 3,
                    ["name"] = "FPS",
                    ["color"] = "#00FF00",
                    ["unit"] = "fps",
                    ["type"] = "frames"
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_EVENTS",
            ["events"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = $"{sessionId}-start",
                    ["label"] = "checkout.started",
                    ["eventType"] = "Trace",
                    ["capturedAtUtc"] = spanStartedUtc,
                    ["channel"] = 4
                },
                new JsonObject
                {
                    ["id"] = $"{sessionId}-end",
                    ["label"] = "checkout.completed",
                    ["eventType"] = "Trace",
                    ["capturedAtUtc"] = spanCompletedUtc,
                    ["channel"] = 4
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_METRICS",
            ["metrics"] = new JsonArray
            {
                new JsonObject
                {
                    ["channel"] = 3,
                    ["value"] = framesPerSecond,
                    ["capturedAtUtc"] = spanStartedUtc.AddMilliseconds(250)
                },
                new JsonObject
                {
                    ["channel"] = 3,
                    ["value"] = framesPerSecond,
                    ["capturedAtUtc"] = spanStartedUtc.AddMilliseconds(750)
                }
            }
        });
        await SendJsonAsync(socket, new JsonObject
        {
            ["type"] = "CLIENT_DONE",
            ["data"] = "done"
        });
        await DrainSocketAsync(socket);

        await TestWait.UntilAsync(
            () => captureEvents.Any(runtimeEvent =>
                runtimeEvent.SessionId == sessionId
                && runtimeEvent.Kind == RuntimeSessionCaptureEventKind.Finalized),
            timeout: TimeSpan.FromSeconds(15),
            because: "Automatic trends evaluation should begin after capture sources are finalized.");
        return sessionId!;
    }

    private static async Task<WorkspaceTrendsReport> WaitForAutomaticTrendsReportAsync(
        RuntimeCoordinator runtime,
        string appId,
        string sessionId)
    {
        var reportPath = Path.Combine(
            runtime.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            FileNameUtil.Sanitize(appId),
            FileNameUtil.Sanitize(sessionId),
            "trends-results.json");
        await TestWait.UntilAsync(
            () => File.Exists(reportPath),
            timeout: TimeSpan.FromSeconds(15),
            because: "The finalized session should receive an automatic trends sidecar.");
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Deserialize<WorkspaceTrendsReport>(
                   await File.ReadAllTextAsync(reportPath),
                   serializerOptions)
               ?? throw new InvalidDataException("The automatic trends report could not be read.");
    }

    private static void WriteAutomaticTrendsWorkspace(string workspacePath, string appId)
    {
        var trendsPath = Path.Combine(workspacePath, "ansight", "trends");
        Directory.CreateDirectory(trendsPath);
        File.WriteAllText(Path.Combine(trendsPath, "checkout.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "checkout-fps",
              "appId": "{{appId}}",
              "span": {
                "start": { "event": { "label": "checkout.started" } },
                "end": { "event": { "label": "checkout.completed" } }
              },
              "metrics": [{
                "id": "average",
                "channel": { "type": "fps" },
                "statistic": "average",
                "minimumSamples": 2,
                "budget": { "gte": 30 },
                "regression": {
                  "percent": 10,
                  "confirmRuns": 1,
                  "minimumBaselineRuns": 1,
                  "seriesBy": ["platform"]
                },
                "display": {
                  "title": "Checkout frame rate",
                  "unit": "fps",
                  "includeZero": true
                }
              }]
            }
            """);
    }
}
