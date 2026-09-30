using Ansight.Host.Runtime.Operations;
using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure;
using Ansight.Tools;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionTrimOperationTests
{
    [Fact]
    public void ToolCatalog_IncludesSessionTrimTools()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);

        var tools = catalog.BuildToolsListResult()["tools"]!.AsArray();
        var toolNames = tools
            .Select(tool => tool!["name"]!.GetValue<string>())
            .ToArray();

        Assert.Contains("ansight_trim_session_remove_range", toolNames);
        Assert.Contains("ansight_trim_session_keep_range", toolNames);
        Assert.Contains("ansight_force_disconnect_session", toolNames);
        Assert.Contains("ansight_delete_session", toolNames);
        Assert.Contains("ansight_export_session_archive", toolNames);
        Assert.Contains("ansight_import_session_archive", toolNames);
        Assert.Contains("ansight_delete_session_analysis", toolNames);
        Assert.Contains("ansight_get_session_storage", toolNames);

        var removeTool = tools
            .Select(tool => tool!.AsObject())
            .Single(tool => string.Equals(tool["name"]!.GetValue<string>(), "ansight_trim_session_remove_range", StringComparison.Ordinal));
        var required = removeTool["inputSchema"]!["required"]!
            .AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.Contains("startUtc", required);
        Assert.Contains("endUtc", required);
    }

    [Fact]
    public async Task ForceDisconnectSessionTool_DisconnectsLiveSession()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var appToolBridge = new TestAppToolBridge();
        var sessionId = runtimeState.CreateSession("com.example.live", "Live Client", IPAddress.Loopback, null, null);
        appToolBridge.Connect(sessionId);
        var catalog = CreateToolCatalog(runtimeState, environment, appToolBridge);

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_force_disconnect_session",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal(sessionId, payload["sessionId"]!.GetValue<string>());
        Assert.False(appToolBridge.IsSessionConnected(sessionId));
    }

    [Fact]
    public async Task SessionManagementTools_WorkAcrossCapturedSession()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        var importResult = ImportTimelineSession(runtimeState, "operation-management-001", startedAtUtc);
        Assert.True(importResult.IsSuccess);
        var sessionId = importResult.ImportedSession!.SessionId;

        var storageResponse = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_get_session_storage",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId
                }
            });
        var storagePayload = GetStructuredContent(storageResponse);
        Assert.True(storagePayload["cacheSizeAvailable"]!.GetValue<bool>());
        Assert.True(storagePayload["cacheSizeBytes"]!.GetValue<long>() > 0);
        Assert.True(storagePayload["files"]!["sessionDirectoryExists"]!.GetValue<bool>());

        var deleteAnalysisResponse = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_delete_session_analysis",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["analysisId"] = "analysis-001",
                    ["expectedAgentId"] = "codex"
                }
            });
        var deleteAnalysisPayload = GetStructuredContent(deleteAnalysisResponse);
        Assert.Equal("analysis-001", deleteAnalysisPayload["analysisId"]!.GetValue<string>());
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var updatedSnapshot));
        Assert.NotNull(updatedSnapshot);
        Assert.Empty(updatedSnapshot!.Analyses);

        var archiveFilePath = Path.Combine(environment.RootPath, "operation-management-session.zip");
        var exportResponse = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_export_session_archive",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["archiveFilePath"] = archiveFilePath
                }
            });
        var exportPayload = GetStructuredContent(exportResponse);
        Assert.Equal(archiveFilePath, exportPayload["archiveFilePath"]!.GetValue<string>());
        Assert.True(File.Exists(archiveFilePath));
        Assert.True(exportPayload["archiveSizeBytes"]!.GetValue<long>() > 0);

        string importedSessionId;
        var importArchiveResponse = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_import_session_archive",
                ["arguments"] = new JsonObject
                {
                    ["archiveFilePath"] = archiveFilePath,
                    ["replaySourceKind"] = "operation-test",
                    ["replaySourceDisplayName"] = "Operation test import"
                }
            });
        var importArchivePayload = GetStructuredContent(importArchiveResponse);
        importedSessionId = importArchivePayload["importedSession"]!["sessionId"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(importedSessionId));
        Assert.True(runtimeState.TryGetSessionSnapshot(importedSessionId, out var importedSnapshot));
        Assert.NotNull(importedSnapshot);
        Assert.Equal(3, importedSnapshot!.Logs.Count);
        Assert.Equal("operation-test", importedSnapshot.ReplaySource?.Kind);

        var deleteSessionResponse = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_delete_session",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["confirmDelete"] = true,
                    ["expectedAppId"] = "com.example.operationtrim"
                }
            });
        var deleteSessionPayload = GetStructuredContent(deleteSessionResponse);
        Assert.Equal(sessionId, deleteSessionPayload["sessionId"]!.GetValue<string>());
        Assert.False(runtimeState.TryGetSessionSnapshot(sessionId, out _));
    }

    [Fact]
    public async Task RemoveRangeTool_RemovesSelectedTimelineRange()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var importResult = ImportTimelineSession(runtimeState, "operation-trim-remove-001", startedAtUtc);
        Assert.True(importResult.IsSuccess);

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_trim_session_remove_range",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = importResult.ImportedSession!.SessionId,
                    ["startUtc"] = At(10).ToString("O"),
                    ["endUtc"] = At(20).ToString("O")
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal("removeSelectedRange", payload["operation"]!.GetValue<string>());
        Assert.Equal(3, payload["beforeCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(2, payload["afterCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(1, payload["removedCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(2, payload["afterCounts"]!["metricSamples"]!.GetValue<int>());

        Assert.True(runtimeState.TryGetSessionSnapshot(importResult.ImportedSession.SessionId, out var updatedSnapshot));
        Assert.NotNull(updatedSnapshot);
        Assert.Equal(["evt-005", "evt-025"], updatedSnapshot!.Logs.Select(log => log.EventId ?? string.Empty).ToArray());
        Assert.Equal([5, 25], updatedSnapshot.Metrics.Select(metric => metric.Value).ToArray());
    }

    [Fact]
    public async Task KeepRangeTool_RemovesTimelineDataOutsideSelectedRange()
    {
        using var environment = new TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var catalog = CreateToolCatalog(runtimeState, environment);
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        var importResult = ImportTimelineSession(runtimeState, "operation-trim-keep-001", startedAtUtc);
        Assert.True(importResult.IsSuccess);

        var response = await catalog.HandleToolsCallAsync(
            new JsonObject
            {
                ["name"] = "ansight_trim_session_keep_range",
                ["arguments"] = new JsonObject
                {
                    ["sessionId"] = importResult.ImportedSession!.SessionId,
                    ["startUtc"] = At(10).ToString("O"),
                    ["endUtc"] = At(20).ToString("O")
                }
            });

        var payload = GetStructuredContent(response);
        Assert.Equal("keepOnlySelectedRange", payload["operation"]!.GetValue<string>());
        Assert.Equal(3, payload["beforeCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(1, payload["afterCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(2, payload["removedCounts"]!["logs"]!.GetValue<int>());
        Assert.Equal(At(10), payload["session"]!["createdUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(At(20), payload["session"]!["lastUpdatedUtc"]!.GetValue<DateTimeOffset>());

        Assert.True(runtimeState.TryGetSessionSnapshot(importResult.ImportedSession.SessionId, out var updatedSnapshot));
        Assert.NotNull(updatedSnapshot);
        Assert.Equal(At(10), updatedSnapshot!.CreatedUtc);
        Assert.Equal(At(20), updatedSnapshot.LastUpdatedUtc);
        Assert.Equal(["evt-015"], updatedSnapshot.Logs.Select(log => log.EventId ?? string.Empty).ToArray());
        Assert.Equal([15], updatedSnapshot.Metrics.Select(metric => metric.Value).ToArray());
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

    private static SessionImportResult ImportTimelineSession(
        RuntimeState runtimeState,
        string sessionId,
        DateTimeOffset startedAtUtc)
    {
        DateTimeOffset At(int seconds) => startedAtUtc.AddSeconds(seconds);
        return runtimeState.ImportSessionSnapshot(
            new AppSessionSnapshot
            {
                SessionId = sessionId,
                AppId = "com.example.operationtrim",
                ClientName = "Operation Trim Test Client",
                RemoteAddress = "127.0.0.1",
                CreatedUtc = startedAtUtc,
                ConfigId = null,
                Status = "WebSocket Closed",
                LastUpdatedUtc = At(30),
                IsHistorical = true,
                Analyses =
                [
                    new SessionAnalysisRecord
                    {
                        AnalysisId = "analysis-001",
                        AgentId = "codex",
                        StartedUtc = At(21),
                        CompletedUtc = At(22),
                        Success = true
                    }
                ],
                Logs =
                [
                    CreateLog(At(5), "evt-005"),
                    CreateLog(At(15), "evt-015"),
                    CreateLog(At(25), "evt-025")
                ],
                MetricChannels = [CreateMetricChannel()],
                Metrics =
                [
                    CreateMetric(5, At(5)),
                    CreateMetric(15, At(15)),
                    CreateMetric(25, At(25))
                ]
            },
            new Dictionary<string, byte[]>());
    }

    private static JsonObject GetStructuredContent(RequestResult response)
    {
        Assert.False(response.IsError);
        Assert.NotNull(response.Payload);
        Assert.False(response.Payload!["isError"]!.GetValue<bool>());
        return Assert.IsType<JsonObject>(response.Payload["structuredContent"]);
    }

    private static SessionMetricChannel CreateMetricChannel()
    {
        return new SessionMetricChannel
        {
            ChannelId = 1,
            Name = "FPS",
            ColorHex = "#00FF00"
        };
    }

    private static SessionMetricSample CreateMetric(long value, DateTimeOffset capturedAtUtc)
    {
        return new SessionMetricSample
        {
            ChannelId = 1,
            Value = value,
            CapturedAtUtc = capturedAtUtc,
            SegmentId = 1
        };
    }

    private static LogEntry CreateLog(DateTimeOffset timestampUtc, string eventId)
    {
        return new LogEntry(timestampUtc, $"Event {eventId}")
        {
            Source = "Client",
            EventId = eventId
        };
    }

    private sealed class TestAppToolBridge : IAppToolBridge
    {
        private readonly HashSet<string> connectedSessionIds = new(StringComparer.Ordinal);

        public event EventHandler? ConnectionsChanged
        {
            add { }
            remove { }
        }

        public void Connect(string sessionId)
        {
            connectedSessionIds.Add(sessionId);
        }

        public IReadOnlyList<string> GetConnectedSessionIds()
            => connectedSessionIds.ToArray();

        public bool IsSessionConnected(string sessionId)
            => connectedSessionIds.Contains(sessionId);

        public OperationResult ForceDisconnectSession(string sessionId)
        {
            return connectedSessionIds.Remove(sessionId)
                ? OperationResult.Success($"Session '{sessionId}' disconnected.")
                : OperationResult.Failure($"Session '{sessionId}' is not connected.");
        }

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
