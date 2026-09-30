using System.IO.Compression;
using System.Text.Json;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class SessionLogStreamsTests
{
    [Fact]
    public void Normalize_MapsLegacyLogsToSdkStream()
    {
        var log = new LogEntry(DateTimeOffset.Parse("2026-07-14T03:04:05Z"), "hello");

        var streams = SessionLogStreams.Normalize(Array.Empty<SessionLogStream>(), new[] { log });

        var stream = Assert.Single(streams);
        Assert.Equal(SessionLogStreamIds.AnsightSdk, stream.StreamId);
        Assert.Equal(SessionLogStreamKinds.AnsightSdk, stream.Kind);
        Assert.Equal(SessionLogStreamIds.AnsightSdk, Assert.Single(stream.Entries).StreamId);
    }

    [Fact]
    public void Normalize_RecoversStreamGroupsWhenDescriptorsAreMissing()
    {
        var timestamp = DateTimeOffset.Parse("2026-07-14T03:04:05Z");
        var logs = new[]
        {
            new LogEntry(timestamp, "sdk"),
            new LogEntry(timestamp, "native") { StreamId = SessionLogStreamIds.AndroidLogcat }
        };

        var streams = SessionLogStreams.Normalize(Array.Empty<SessionLogStream>(), logs);

        Assert.Equal(2, streams.Count);
        Assert.Equal("sdk", Assert.Single(streams.Single(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk).Entries).Message);
        Assert.Equal("native", Assert.Single(streams.Single(stream => stream.StreamId == SessionLogStreamIds.AndroidLogcat).Entries).Message);
    }

    [Fact]
    public void Flatten_OrdersEntriesAcrossStreamsAndPreservesStreamIdentity()
    {
        var later = DateTimeOffset.Parse("2026-07-14T03:04:06Z");
        var earlier = later.AddSeconds(-1);
        var streams = new[]
        {
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AnsightSdk,
                Kind = SessionLogStreamKinds.AnsightSdk,
                DisplayName = "Ansight SDK",
                Entries = new[] { new LogEntry(later, "sdk") }
            },
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AndroidLogcat,
                Kind = SessionLogStreamKinds.AndroidLogcat,
                DisplayName = "Android Logcat",
                Entries = new[] { new LogEntry(earlier, "native") }
            }
        };

        var logs = SessionLogStreams.Flatten(streams);

        Assert.Equal(new[] { "native", "sdk" }, logs.Select(log => log.Message));
        Assert.Equal(SessionLogStreamIds.AndroidLogcat, logs[0].StreamId);
        Assert.Equal(SessionLogStreamIds.AnsightSdk, logs[1].StreamId);
    }

    [Fact]
    public void RuntimeState_AppendsNativeEntriesToIndependentStreamAndFlatProjection()
    {
        using var environment = new TestSupport.TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        SessionLogBatchEventArgs? emittedBatch = null;
        runtimeState.SessionLogsAdded += (_, batch) => emittedBatch = batch;
        var sessionId = runtimeState.CreateSession("com.example.app", "Client", IPAddress.Loopback, null, null);
        runtimeState.EnsureSessionLogStream(sessionId, new SessionLogStream
        {
            StreamId = SessionLogStreamIds.AndroidLogcat,
            Kind = SessionLogStreamKinds.AndroidLogcat,
            DisplayName = "Android Logcat",
            Status = SessionLogStreamStatuses.Pending
        });

        runtimeState.AddSessionLogEntries(sessionId, SessionLogStreamIds.AndroidLogcat, new[]
        {
            new LogEntry(DateTimeOffset.Parse("2026-07-14T03:04:05Z"), "native")
            {
                Source = "Android"
            }
        });

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var snapshot));
        Assert.NotNull(snapshot);
        var stream = Assert.Single(snapshot!.LogStreams, value => value.StreamId == SessionLogStreamIds.AndroidLogcat);
        Assert.Equal(SessionLogStreamStatuses.Active, stream.Status);
        Assert.Equal("native", Assert.Single(stream.Entries).Message);
        Assert.Equal(SessionLogStreamIds.AndroidLogcat, Assert.Single(snapshot.Logs).StreamId);
        Assert.NotNull(emittedBatch);
        Assert.Equal(sessionId, emittedBatch!.SessionId);
        Assert.Equal(SessionLogStreamIds.AndroidLogcat, emittedBatch.StreamId);
        Assert.Equal(1, emittedBatch.StreamEntryCount);
        Assert.Equal(1, emittedBatch.TotalEntryCount);
        Assert.Equal("native", Assert.Single(emittedBatch.Entries).Message);
    }

    [Fact]
    public void RuntimeState_LiveContentSnapshotIncludesTelemetryWithoutCopyingLogs()
    {
        using var environment = new TestSupport.TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession("com.example.app", "Client", IPAddress.Loopback, null, null);
        runtimeState.AddSessionLog(sessionId, new LogEntry(DateTimeOffset.UtcNow, "log entry"));
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

        Assert.True(runtimeState.TryGetSessionLiveContentSnapshot(sessionId, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Logs);
        Assert.All(snapshot.LogStreams, stream => Assert.Empty(stream.Entries));
        Assert.Single(snapshot.MetricChannels);
        Assert.Single(snapshot.Metrics);
    }

    [Fact]
    public void RuntimeState_LiveContentSnapshotBoundsTelemetryWhileFullSnapshotRetainsHistory()
    {
        using var environment = new TestSupport.TestEnvironment();
        var runtimeState = new RuntimeState(new SessionCaptureStore(environment.ApplicationPaths));
        var sessionId = runtimeState.CreateSession("com.example.app", "Client", IPAddress.Loopback, null, null);
        var latestUtc = DateTimeOffset.Parse("2026-07-27T06:00:00Z");
        runtimeState.AddSessionMetrics(sessionId, new[]
        {
            CreateMetric(latestUtc.AddMinutes(-3), 30),
            CreateMetric(latestUtc, 60),
            CreateMetric(latestUtc.AddSeconds(-30), 55)
        }, telemetrySegmentId: 1);

        Assert.True(runtimeState.TryGetSessionLiveContentSnapshot(sessionId, out var liveSnapshot));
        Assert.NotNull(liveSnapshot);
        Assert.Equal(new long[] { 55, 60 }, liveSnapshot!.Metrics.Select(metric => metric.Value));

        Assert.True(runtimeState.TryGetSessionReplaySnapshot(sessionId, out var replaySnapshot));
        Assert.NotNull(replaySnapshot);
        Assert.Equal(3, replaySnapshot!.Metrics.Count);

        Assert.True(runtimeState.TryGetSessionSnapshot(sessionId, out var fullSnapshot));
        Assert.NotNull(fullSnapshot);
        Assert.Equal(3, fullSnapshot!.Metrics.Count);
    }

    [Fact]
    public void SessionState_LiveReplaySnapshotBoundsTelemetryAcrossFullHistory()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-27T00:00:00Z");
        var state = new SessionState
        {
            SessionId = "live-replay-metric-window",
            AppId = "com.example.app",
            ClientName = "Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            LastUpdatedUtc = createdUtc,
            Status = "WebSocket Open",
            ConfigId = null
        };
        var metrics = Enumerable.Range(0, 21_001)
            .SelectMany(index => new[]
            {
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = index == 10_000 ? 10_000 : 60,
                    CapturedAtUtc = createdUtc.AddSeconds(index),
                    SegmentId = 1
                },
                new SessionMetricSample
                {
                    ChannelId = 2,
                    Value = index == 10_000 ? 1 : 400,
                    CapturedAtUtc = createdUtc.AddSeconds(index),
                    SegmentId = 1
                }
            })
            .ToArray();

        state.AddMetrics(metrics);

        var replayMetrics = state.GetLiveReplaySnapshotMetrics();
        var liveMetrics = state.GetLiveSnapshotMetrics();

        Assert.Equal(40_000, replayMetrics.Count);
        Assert.Equal(metrics.Length, state.GetSnapshotMetrics().Count);
        Assert.True(liveMetrics.Count < replayMetrics.Count);
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 1 && sample.CapturedAtUtc == createdUtc);
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 2 && sample.CapturedAtUtc == createdUtc);
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 1 && sample.CapturedAtUtc == createdUtc.AddSeconds(21_000));
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 2 && sample.CapturedAtUtc == createdUtc.AddSeconds(21_000));
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 1 && sample.Value == 10_000);
        Assert.Contains(replayMetrics, sample => sample.ChannelId == 2 && sample.Value == 1);
    }

    [Fact]
    public void SessionState_LiveReplaySnapshotBoundsLogsAndPreservesAbsoluteCursor()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-17T01:00:00Z");
        var state = new SessionState
        {
            SessionId = "live-replay-log-window",
            AppId = "com.example.app",
            ClientName = "Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            LastUpdatedUtc = createdUtc,
            Status = "WebSocket Open",
            ConfigId = null
        };
        var entries = Enumerable.Range(0, 10_005)
            .Select(index => new LogEntry(createdUtc.AddMilliseconds(index), $"log-{index}")
            {
                StreamId = SessionLogStreamIds.AnsightSdk
            })
            .ToArray();
        state.Logs.AddRange(entries);
        var stream = new SessionLogStreamState
        {
            StreamId = SessionLogStreamIds.AnsightSdk,
            Kind = SessionLogStreamKinds.AnsightSdk,
            DisplayName = "Ansight SDK",
            Status = SessionLogStreamStatuses.Active,
            TotalEntryCount = entries.Length
        };
        stream.Entries.AddRange(entries);
        state.LogStreams[stream.StreamId] = stream;
        state.TotalLogCount = entries.Length;
        state.MarkLogsChanged();

        var logs = state.GetLiveReplaySnapshotLogs();
        var replayStream = Assert.Single(state.GetLiveReplaySnapshotLogStreams());

        Assert.Equal(10_000, logs.Count);
        Assert.Equal("log-5", logs[0].Message);
        Assert.Equal("log-10004", logs[^1].Message);
        Assert.Equal(10_000, replayStream.Entries.Count);
        Assert.Equal(5, replayStream.RetainedEntryStartIndex);
        Assert.Equal(10_005, replayStream.TotalEntryCount);
    }

    private static SessionMetricSample CreateMetric(DateTimeOffset capturedAtUtc, long value)
    {
        return new SessionMetricSample
        {
            ChannelId = 1,
            Value = value,
            CapturedAtUtc = capturedAtUtc
        };
    }

    [Fact]
    public void ArchiveCodec_RoundTripsIndependentStreamDocuments()
    {
        var streams = new[]
        {
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AppleUnifiedLog,
                Kind = SessionLogStreamKinds.AppleUnifiedLog,
                DisplayName = "Apple Unified Log",
                Status = SessionLogStreamStatuses.Completed,
                StatusMessage = "Session ended.",
                Metadata = new Dictionary<string, string> { ["deviceUdid"] = "SIM-001" },
                Entries = new[]
                {
                    new LogEntry(DateTimeOffset.Parse("2026-07-14T03:04:05Z"), "native")
                    {
                        Source = "com.example.app"
                    }
                }
            }
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var storage = new MemoryStream();
        using (var archive = new ZipArchive(storage, ZipArchiveMode.Create, leaveOpen: true))
        {
            SessionLogStreamArchiveCodec.Write(archive, streams, DateTimeOffset.UtcNow, options);
        }

        storage.Position = 0;
        using var readArchive = new ZipArchive(storage, ZipArchiveMode.Read);
        var restored = SessionLogStreamArchiveCodec.TryRead(readArchive, options);

        Assert.NotNull(restored);
        var stream = Assert.Single(restored!);
        Assert.Equal(SessionLogStreamIds.AppleUnifiedLog, stream.StreamId);
        Assert.Equal("SIM-001", stream.Metadata["deviceUdid"]);
        Assert.Equal(SessionLogStreamIds.AppleUnifiedLog, Assert.Single(stream.Entries).StreamId);
        Assert.NotNull(readArchive.GetEntry(SessionLogStreamArchiveCodec.IndexEntryPath));
    }
}
