using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
    [Fact]
    public void CreateSession_IncludesAppHintInIssuedSessionId()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);

        var sessionId = runtimeState.CreateSession("com.Example.Target App", "Client", IPAddress.Loopback, null, null);

        Assert.Equal("com-example-target-app-001", sessionId);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal("com.Example.Target App", snapshot!.AppId);
    }

    [Fact]
    public void ReserveSessionId_IncludesAppHintAndRemainsUsableForCreateSession()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);

        var reservedSessionId = runtimeState.ReserveSessionId("com.Example.Reserved App", null);
        var sessionId = runtimeState.CreateSession(
            "com.Example.Reserved App",
            "Client",
            IPAddress.Loopback,
            null,
            null,
            reservedSessionId);
        var nextSessionId = runtimeState.CreateSession("com.example.other", "Client", IPAddress.Loopback, null, null);

        Assert.Equal("com-example-reserved-app-001", reservedSessionId);
        Assert.Equal(reservedSessionId, sessionId);
        Assert.Equal("com-example-other-002", nextSessionId);
    }

    [Fact]
    public void CreateSession_DoesNotReuseDeletedIdBeforeSequenceInitialization()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        captureStore.Save(new AppSessionSnapshot
        {
            SessionId = "com-example-app-001",
            AppId = "com.example.app",
            ClientName = "Client",
            RemoteAddress = IPAddress.Loopback.ToString(),
            CreatedUtc = DateTimeOffset.Parse("2026-09-02T21:00:00Z"),
            ConfigId = null,
            Status = "WebSocket Closed",
            LastUpdatedUtc = DateTimeOffset.Parse("2026-09-02T21:01:00Z"),
            IsHistorical = true,
            MetricChannels = [],
            Metrics = []
        });
        var runtimeState = new RuntimeState(captureStore);

        var deleteResult = runtimeState.DeleteSession("com-example-app-001");
        var sessionId = runtimeState.CreateSession(
            "com.example.app",
            "Client",
            IPAddress.Loopback,
            null,
            null);

        Assert.True(deleteResult.IsSuccess);
        Assert.Equal("com-example-app-002", sessionId);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out _));
    }

    [Fact]
    public async Task SetSessionAppState_PublishesAndPersistsDistinctClientLifecycleEvents()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var lifecycleEvents = new ConcurrentQueue<RuntimeClientAppStateChangedEvent>();
        runtimeState.RuntimeEventOccurred += (_, runtimeEvent) =>
        {
            if (runtimeEvent is RuntimeClientAppStateChangedEvent lifecycleEvent)
            {
                lifecycleEvents.Enqueue(lifecycleEvent);
            }
        };

        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);

        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Foreground, DateTimeOffset.Parse("2026-03-22T01:00:00Z"));
        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Foreground, DateTimeOffset.Parse("2026-03-22T01:05:00Z"));
        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Background, DateTimeOffset.Parse("2026-03-22T01:10:00Z"));
        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Foreground, DateTimeOffset.Parse("2026-03-22T01:20:00Z"));
        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Background, DateTimeOffset.Parse("2026-03-22T01:30:00Z"));
        runtimeState.AddSessionApplicationEvents(sessionId,
        [
            new SessionApplicationEvent(
                "sdk-lifecycle-foreground",
                "lifecycle.foreground",
                "Lifecycle",
                string.Empty,
                DateTimeOffset.Parse("2026-03-22T01:00:00Z"),
                0)
        ]);

        var publishedEvents = lifecycleEvents.ToArray();
        Assert.All(publishedEvents, runtimeEvent => Assert.Equal(sessionId, runtimeEvent.SessionId));
        Assert.Equal(
            [AppLifecycleState.Unknown, AppLifecycleState.Foreground, AppLifecycleState.Background, AppLifecycleState.Foreground],
            publishedEvents.Select(static runtimeEvent => runtimeEvent.PreviousState));
        Assert.Equal(
            [AppLifecycleState.Foreground, AppLifecycleState.Background, AppLifecycleState.Foreground, AppLifecycleState.Background],
            publishedEvents.Select(static runtimeEvent => runtimeEvent.CurrentState));
        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-03-22T01:00:00Z"),
                DateTimeOffset.Parse("2026-03-22T01:10:00Z"),
                DateTimeOffset.Parse("2026-03-22T01:20:00Z"),
                DateTimeOffset.Parse("2026-03-22T01:30:00Z")
            ],
            publishedEvents.Select(static runtimeEvent => runtimeEvent.ChangedAtUtc));

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(
            ["lifecycle.foreground", "lifecycle.background", "lifecycle.foreground", "lifecycle.background"],
            snapshot!.ApplicationEvents.Select(static appEvent => appEvent.Label));
        Assert.Equal("sdk-lifecycle-foreground", snapshot.ApplicationEvents[0].EventId);

        var timelineStates = SessionTimelineBuilder.BuildEvents(snapshot, startUtc: null, endUtc: null)
            .Where(static item => item.Payload["category"]?.GetValue<string>() == "appState")
            .Select(static item => item.Payload["details"]?["appState"]?.GetValue<string>() ?? string.Empty)
            .ToArray();
        Assert.Equal(["Foreground", "Background", "Foreground", "Background"], timelineStates);

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionId, out var persistedSnapshot)
                  && persistedSnapshot?.ApplicationEvents.Count == 4,
            because: "Every distinct lifecycle transition should be persisted.");

        var reloadedRuntimeState = new RuntimeState(captureStore);
        Assert.True(reloadedRuntimeState.TryGetSessionSnapshot(sessionId, out var reloadedSnapshot));
        Assert.Equal(
            ["lifecycle.foreground", "lifecycle.background", "lifecycle.foreground", "lifecycle.background"],
            reloadedSnapshot?.ApplicationEvents.Select(static appEvent => appEvent.Label));

        // External watches have no SDK lifecycle log stream. Live replay must
        // receive their complete marker history independently of log cursors.
        runtimeState.AddSessionApplicationEvents(sessionId,
            [new SessionApplicationEvent("run-start", "run.started", "host.run.started", "", DateTimeOffset.UtcNow, 0)]);
        Assert.True(runtimeState.TryGetSessionLiveUpdate(sessionId,
            new SessionLiveUpdateCursor(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), out var update));
        Assert.Empty(update!.Logs);
        Assert.Equal(snapshot.ApplicationEvents, update.LifecycleEvents);
    }

    [Fact]
    public void SessionTimelineBuilder_UsesCanonicalLifecycleLogsForHistoricalTransitions()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-03-22T01:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "app-lifecycle-001",
            AppId = "com.example.app",
            ClientName = "Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = startedAtUtc,
            ConfigId = null,
            Status = "WebSocket Closed",
            LastUpdatedUtc = startedAtUtc.AddMinutes(30),
            IsHistorical = true,
            AppState = AppLifecycleState.Background,
            AppStateChangedUtc = startedAtUtc.AddMinutes(30),
            Logs =
            [
                LifecycleLog(startedAtUtc, "lifecycle.foreground"),
                LifecycleLog(startedAtUtc.AddMinutes(10), "lifecycle.background"),
                LifecycleLog(startedAtUtc.AddMinutes(20), "App moved to foreground"),
                LifecycleLog(startedAtUtc.AddMinutes(30), "lifecycle.background")
            ],
            MetricChannels = [],
            Metrics = []
        };

        var timelineStates = SessionTimelineBuilder.BuildEvents(snapshot, startUtc: null, endUtc: null)
            .Where(static item => item.Payload["category"]?.GetValue<string>() == "appState")
            .Select(static item => item.Payload["details"]?["appState"]?.GetValue<string>() ?? string.Empty)
            .ToArray();

        Assert.Equal(["Foreground", "Background", "Foreground", "Background"], timelineStates);
    }

    private static LogEntry LifecycleLog(DateTimeOffset timestampUtc, string message)
        => new(timestampUtc, message)
        {
            Tag = "LIFECYCLE"
        };

    [Fact]
    public async Task SetSessionCustomProperties_UpdatesAndPersistsGroupedProperties()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        var customProperties = new JsonObject
        {
            ["app"] = new JsonObject
            {
                ["tenant"] = "acme",
                ["region"] = "au"
            },
            ["flags"] = new JsonObject
            {
                ["beta"] = true
            }
        };

        runtimeState.SetSessionCustomProperties(sessionId, customProperties);
        customProperties["app"]!["tenant"] = "mutated";

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.Equal("acme", snapshot?.CustomProperties?["app"]?["tenant"]?.GetValue<string>());
        Assert.True(snapshot?.CustomProperties?["flags"]?["beta"]?.GetValue<bool>());

        Assert.True(runtimeState.TryGetSessionLiveUpdate(sessionId,
            new SessionLiveUpdateCursor(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), out var update));
        Assert.Equal("acme", update!.CustomProperties?["app"]?["tenant"]?.GetValue<string>());
        update.CustomProperties!["app"]!["tenant"] = "live-update-mutation";
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var unchangedSnapshot));
        Assert.Equal("acme", unchangedSnapshot!.CustomProperties?["app"]?["tenant"]?.GetValue<string>());

        await TestSupport.TestWait.UntilAsync(
            () =>
            {
                if (!captureStore.TryLoad(sessionId, out var persistedSnapshot))
                {
                    return false;
                }

                return string.Equals(
                    persistedSnapshot?.CustomProperties?["app"]?["tenant"]?.GetValue<string>(),
                    "acme",
                    StringComparison.Ordinal);
            },
            because: "The persisted capture should include custom session properties.");
    }

    [Fact]
    public async Task SessionUpdated_CoalescesRapidMutationsForTheSameSession()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var snapshots = new ConcurrentQueue<AppSessionSnapshot>();
        runtimeState.SessionUpdated += (_, snapshot) => snapshots.Enqueue(snapshot);

        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId),
            because: "Initial session snapshot should be published.");

        while (snapshots.TryDequeue(out _))
        {
        }

        runtimeState.SetSessionStatus(sessionId, "Connect Accepted");
        runtimeState.SetSessionAppState(sessionId, AppLifecycleState.Foreground, DateTimeOffset.Parse("2026-03-22T01:00:00Z"));
        runtimeState.UpdateSessionMetricChannels(sessionId, new[]
        {
            new SessionMetricChannel
            {
                ChannelId = 1,
                Name = "FPS",
                ColorHex = "#00FF00"
            }
        });
        runtimeState.AddSessionMetrics(sessionId, new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 60,
                CapturedAtUtc = DateTimeOffset.UtcNow
            }
        }, telemetrySegmentId: 1);

        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Count == 1,
            because: "Rapid mutations for a single session should coalesce into one SessionUpdated snapshot.");
        await Task.Delay(200);

        Assert.Single(snapshots);
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Connect Accepted", snapshot.Status);
        Assert.Equal(AppLifecycleState.Foreground, snapshot.AppState);
        Assert.Empty(snapshot.MetricChannels);
        Assert.Empty(snapshot.Metrics);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var fullSnapshot));
        Assert.NotNull(fullSnapshot);
        Assert.Single(fullSnapshot!.MetricChannels);
        Assert.Single(fullSnapshot.Metrics);
    }

    [Fact]
    public async Task UpdateSessionMetadata_PersistsTagsNotesAndPinState()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var snapshots = new ConcurrentQueue<AppSessionSnapshot>();
        runtimeState.SessionUpdated += (_, snapshot) => snapshots.Enqueue(snapshot);

        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, null);
        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId),
            because: "Initial session snapshot should be published.");

        while (snapshots.TryDequeue(out _))
        {
        }

        var result = runtimeState.UpdateSessionMetadata(
            sessionId,
            isPinned: true,
            tags: ["crash", "smoke-test", "crash"],
            notes: "  Investigate startup regression.  ",
            name: "  Startup crash  ");

        Assert.True(result.IsSuccess);

        await TestSupport.TestWait.UntilAsync(
            () => snapshots.Any(snapshot => snapshot.SessionId == sessionId && snapshot.IsPinned),
            because: "Metadata updates should publish a session snapshot.");

        var updated = Assert.Single(snapshots);
        Assert.True(updated.IsPinned);
        Assert.Equal("Startup crash", updated.Name);
        Assert.Equal(["crash", "smoke-test"], updated.Tags);
        Assert.Equal("Investigate startup regression.", updated.Notes);

        AppSessionSnapshot? persisted = null;
        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionId, out persisted) && persisted?.IsPinned == true,
            because: "Metadata should be persisted on the slower capture-store cadence.");
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsPinned);
        Assert.Equal("Startup crash", persisted.Name);
        Assert.Equal(["crash", "smoke-test"], persisted.Tags);
        Assert.Equal("Investigate startup regression.", persisted.Notes);
    }

    [Fact]
    public void CreateSession_WithMatchingProcessSessionId_ReusesSessionAndDeduplicatesReplayData()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);

        var firstMetricTimestamp = DateTimeOffset.Parse("2026-03-22T01:00:00Z");
        var sessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, "proc-1");
        runtimeState.AddSessionMetrics(sessionId, new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 60,
                CapturedAtUtc = firstMetricTimestamp
            }
        }, telemetrySegmentId: 1);
        runtimeState.AddSessionLogs(sessionId, new[]
        {
            new LogEntry(firstMetricTimestamp, "Foreground")
            {
                Source = "Client",
                Tag = "LIFECYCLE",
                EventId = "evt-1"
            }
        });
        runtimeState.SetSessionStatus(sessionId, "WebSocket Closed");

        var resumedSessionId = runtimeState.CreateSession("app-1", "Client", IPAddress.Loopback, null, "proc-1");
        var secondSegmentId = runtimeState.BeginSessionConnection(resumedSessionId);
        runtimeState.AddSessionMetrics(resumedSessionId, new[]
        {
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 60,
                CapturedAtUtc = firstMetricTimestamp
            },
            new SessionMetricSample
            {
                ChannelId = 1,
                Value = 58,
                CapturedAtUtc = firstMetricTimestamp.AddSeconds(1)
            }
        }, secondSegmentId);
        runtimeState.AddSessionLogs(resumedSessionId, new[]
        {
            new LogEntry(firstMetricTimestamp, "Foreground")
            {
                Source = "Client",
                Tag = "LIFECYCLE",
                EventId = "evt-1"
            },
            new LogEntry(firstMetricTimestamp.AddSeconds(1), "Background")
            {
                Source = "Client",
                Tag = "LIFECYCLE",
                EventId = "evt-2"
            }
        });

        Assert.Equal(sessionId, resumedSessionId);
        Assert.Equal(2, secondSegmentId);
        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal("proc-1", snapshot!.ProcessSessionId);
        Assert.Equal(2, snapshot.Metrics.Count);
        Assert.Equal([1, 2], snapshot.Metrics.Select(metric => metric.SegmentId).Distinct().OrderBy(value => value).ToArray());
        Assert.Equal(["evt-1", "evt-2"], snapshot.Logs.Select(log => log.EventId).Where(eventId => eventId is not null).Cast<string>().ToArray());
    }

    [Fact]
    public async Task SetSessionStatus_TerminalSessionsBecomeHistoricalAndRespectRetentionLimit()
    {
        using var environment = new TestSupport.TestEnvironment();
        var captureStore = new SessionCaptureStore(environment.ApplicationPaths);
        var runtimeState = new RuntimeState(captureStore);
        var sessionIds = new List<string>();
        var startedAtUtc = DateTimeOffset.Parse("2026-04-01T05:00:00Z");

        for (var index = 0; index < 11; index++)
        {
            var sessionId = runtimeState.CreateSession($"app-{index}", "Client", IPAddress.Loopback, null, null);
            sessionIds.Add(sessionId);
            runtimeState.AddSessionLogs(
                sessionId,
                [
                    new LogEntry(startedAtUtc.AddSeconds(index), $"closed-{index}")
                    {
                        Source = "Client",
                        EventId = $"evt-{index}"
                    }
                ]);
            runtimeState.SetSessionStatus(sessionId, "WebSocket Closed");
        }

        Assert.All(runtimeState.GetSessionSummaries(), snapshot => Assert.True(snapshot.IsHistorical));
        Assert.Equal(10, GetLoadedSessionStateCount(runtimeState));

        await TestSupport.TestWait.UntilAsync(
            () => captureStore.TryLoad(sessionIds[0], out var persistedSnapshot)
                  && persistedSnapshot?.IsHistorical == true
                  && persistedSnapshot.Status == "WebSocket Closed"
                  && persistedSnapshot.Logs.Any(log => string.Equals(log.EventId, "evt-0", StringComparison.Ordinal)),
            because: "Evicted terminal sessions should be persisted before their loaded state is dropped.");

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionIds[0], out var reloadedSnapshot));
        Assert.NotNull(reloadedSnapshot);
        Assert.True(reloadedSnapshot!.IsHistorical);
        Assert.Contains(reloadedSnapshot.Logs, log => string.Equals(log.EventId, "evt-0", StringComparison.Ordinal));
        Assert.Equal(10, GetLoadedSessionStateCount(runtimeState));
    }
}
