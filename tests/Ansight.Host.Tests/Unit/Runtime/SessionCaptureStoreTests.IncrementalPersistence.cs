using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public void Save_RecordingSession_AppendsRecoverableJournalsThenConsolidatesWhenClosed()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var firstLog = new LogEntry(createdUtc.AddSeconds(1), "first log")
        {
            Source = "Client",
            EventId = "log-1"
        };
        var secondLog = new LogEntry(createdUtc.AddSeconds(2), "second log")
        {
            Source = "Client",
            EventId = "log-2"
        };
        var thirdLog = new LogEntry(createdUtc.AddSeconds(3), "third log")
        {
            Source = "Client",
            EventId = "log-3"
        };
        var channel = new SessionMetricChannel
        {
            ChannelId = 1,
            Name = "FPS",
            ColorHex = "#00FF00"
        };
        var firstSample = new SessionMetricSample
        {
            ChannelId = 1,
            Value = 60,
            CapturedAtUtc = createdUtc.AddSeconds(1)
        };
        var secondSample = new SessionMetricSample
        {
            ChannelId = 1,
            Value = 58,
            CapturedAtUtc = createdUtc.AddSeconds(2)
        };
        var thirdSample = new SessionMetricSample
        {
            ChannelId = 1,
            Value = 55,
            CapturedAtUtc = createdUtc.AddSeconds(3)
        };
        var firstTouch = CreateTouch("touch-1", "down", createdUtc.AddSeconds(1), 10, 20);
        var secondTouch = CreateTouch("touch-2", "up", createdUtc.AddSeconds(2), 12, 22);
        var thirdTouch = CreateTouch("touch-3", "down", createdUtc.AddSeconds(3), 14, 24);

        store.Save(new AppSessionSnapshot
        {
            SessionId = "session-live-segments",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(1),
            IsHistorical = false,
            Logs = [firstLog],
            Touches = [firstTouch],
            MetricChannels = [channel],
            Metrics = [firstSample]
        });
        store.Save(new AppSessionSnapshot
        {
            SessionId = "session-live-segments",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = false,
            Logs = [firstLog, secondLog],
            Touches = [firstTouch, secondTouch],
            MetricChannels = [channel],
            Metrics = [firstSample, secondSample]
        });

        var sessionDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-segments");
        var logsAppendFilePath = Path.Combine(sessionDirectoryPath, "logs.jsonl");
        var logSegmentsDirectoryPath = Path.Combine(sessionDirectoryPath, "log-segments");
        var telemetryDirectoryPath = Path.Combine(sessionDirectoryPath, "telemetry");

        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "logs.json")));
        Assert.True(File.Exists(logsAppendFilePath));
        Assert.False(Directory.Exists(logSegmentsDirectoryPath));
        var telemetryAppendFilePath = Path.Combine(telemetryDirectoryPath, "segments.jsonl");
        var touchesAppendFilePath = Path.Combine(sessionDirectoryPath, "touches.jsonl");
        Assert.True(File.Exists(telemetryAppendFilePath));
        Assert.True(File.Exists(touchesAppendFilePath));
        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "touches.json")));
        Assert.Empty(Directory.GetFiles(telemetryDirectoryPath, "channel-001.segment-*.json"));
        File.AppendAllText(logsAppendFilePath, "{\"incomplete\":");
        File.AppendAllText(telemetryAppendFilePath, "{\"incomplete\":");
        File.AppendAllText(touchesAppendFilePath, "{\"incomplete\":");
        store.Save(new AppSessionSnapshot
        {
            SessionId = "session-live-segments",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(3),
            IsHistorical = false,
            Logs = [firstLog, secondLog, thirdLog],
            Touches = [firstTouch, secondTouch, thirdTouch],
            MetricChannels = [channel],
            Metrics = [firstSample, secondSample, thirdSample]
        });
        Assert.True(store.TryLoad("session-live-segments", out var liveSnapshot));
        Assert.NotNull(liveSnapshot);
        Assert.Equal(["log-1", "log-2", "log-3"], liveSnapshot!.Logs.Select(log => log.EventId).Where(eventId => eventId is not null).Cast<string>().ToArray());
        Assert.Equal([60, 58, 55], liveSnapshot.Metrics.Select(metric => metric.Value).ToArray());
        Assert.Equal(["down", "up", "down"], liveSnapshot.Touches.Select(touch => touch.Action).ToArray());

        store.Save(new AppSessionSnapshot
        {
            SessionId = "session-live-segments",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "WebSocket Closed",
            LastUpdatedUtc = createdUtc.AddSeconds(3),
            IsHistorical = false,
            Logs = [firstLog, secondLog, thirdLog],
            Touches = [firstTouch, secondTouch, thirdTouch],
            MetricChannels = [channel],
            Metrics = [firstSample, secondSample, thirdSample]
        });

        Assert.True(File.Exists(Path.Combine(sessionDirectoryPath, "logs.json")));
        Assert.False(File.Exists(logsAppendFilePath));
        Assert.False(Directory.Exists(logSegmentsDirectoryPath));
        Assert.True(File.Exists(Path.Combine(telemetryDirectoryPath, "channel-001.json")));
        Assert.Empty(Directory.GetFiles(telemetryDirectoryPath, "channel-001.segment-*.json"));
        Assert.False(File.Exists(Path.Combine(telemetryDirectoryPath, "segments.jsonl")));
        Assert.True(File.Exists(Path.Combine(sessionDirectoryPath, "touches.json")));
        Assert.False(File.Exists(touchesAppendFilePath));
    }

    [Fact]
    public void Save_StaleRecordingSnapshot_PreservesNewerPendingLogBatch()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-08-13T00:00:00Z");
        var firstLog = CreateLogEntry(
            createdUtc.AddSeconds(1),
            SessionLogStreamIds.AnsightSdk,
            "first");
        var secondLog = CreateLogEntry(
            createdUtc.AddSeconds(2),
            SessionLogStreamIds.AnsightSdk,
            "second");

        store.Save(CreateLogPersistenceSnapshot(createdUtc, "Connected", [], 0, 0));
        store.QueueLogBatch(new SessionLogBatchEventArgs(
            "session-log-tail",
            "com.example.testapp",
            SessionLogStreamIds.AnsightSdk,
            [firstLog, secondLog],
            streamEntryCount: 2,
            totalEntryCount: 2));

        store.Save(CreateLogPersistenceSnapshot(
            createdUtc,
            "Connected",
            [firstLog],
            totalLogCount: 1,
            retainedLogStartIndex: 0));

        var sessionDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-log-tail");
        var logsAppendFilePath = Path.Combine(sessionDirectoryPath, "logs.jsonl");
        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "logs.json")));
        Assert.True(File.Exists(logsAppendFilePath));
        var appendLength = new FileInfo(logsAppendFilePath).Length;

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        restartedStore.Save(CreateLogPersistenceSnapshot(
            createdUtc,
            "Connected",
            [firstLog],
            totalLogCount: 1,
            retainedLogStartIndex: 0));

        Assert.Equal(appendLength, new FileInfo(logsAppendFilePath).Length);
        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "logs.json")));
        Assert.True(restartedStore.TryLoad("session-log-tail", out var loadedSnapshot));
        Assert.Equal(2, loadedSnapshot!.TotalLogCount);
        Assert.Equal(["first", "second"], loadedSnapshot.Logs.Select(entry => entry.Message));

        restartedStore.Save(CreateLogPersistenceSnapshot(
            createdUtc,
            "WebSocket Closed",
            [firstLog],
            totalLogCount: 1,
            retainedLogStartIndex: 0));

        Assert.True(File.Exists(Path.Combine(sessionDirectoryPath, "logs.json")));
        Assert.False(File.Exists(logsAppendFilePath));
        Assert.True(restartedStore.TryLoad("session-log-tail", out var closedSnapshot));
        Assert.Equal(["first", "second"], closedSnapshot!.Logs.Select(entry => entry.Message));
    }

    [Fact]
    public async Task Save_RecordingSession_AppendsImageManifestThenConsolidatesWhenClosed()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-08-13T01:00:00Z");
        var firstFrame = await store.SaveSessionImageAsync(
            "com.example.testapp",
            "session-live-images",
            createdUtc.AddSeconds(1),
            "png",
            10,
            20,
            100,
            new byte[] { 1, 2, 3 });
        var secondFrame = await store.SaveSessionImageAsync(
            "com.example.testapp",
            "session-live-images",
            createdUtc.AddSeconds(2),
            "png",
            10,
            20,
            100,
            new byte[] { 4, 5, 6 });
        Assert.NotNull(firstFrame);
        Assert.NotNull(secondFrame);

        AppSessionSnapshot CreateSnapshot(
            string status,
            IReadOnlyList<SessionImageFrame> images) => new()
        {
            SessionId = "session-live-images",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = status,
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = false,
            Images = images,
            TotalImageCount = images.Count,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(CreateSnapshot("Connected", [firstFrame!]));
        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        restartedStore.Save(CreateSnapshot("Connected", [firstFrame!, secondFrame!]));

        var sessionDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-images");
        var imagesAppendFilePath = Path.Combine(sessionDirectoryPath, "images.jsonl");
        Assert.True(File.Exists(imagesAppendFilePath));
        Assert.Equal(2, File.ReadLines(imagesAppendFilePath).Count());
        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "images.json")));
        Assert.True(restartedStore.TryLoad("session-live-images", out var liveSnapshot));
        Assert.Equal(2, liveSnapshot!.Images.Count);

        restartedStore.Save(CreateSnapshot("WebSocket Closed", [firstFrame!]));

        Assert.True(File.Exists(Path.Combine(sessionDirectoryPath, "images.json")));
        Assert.False(File.Exists(imagesAppendFilePath));
        Assert.True(restartedStore.TryLoad("session-live-images", out var closedSnapshot));
        Assert.Equal(2, closedSnapshot!.Images.Count);
    }

    [Fact]
    public void Save_StaleRecordingSnapshot_DoesNotRewriteTouchOrTelemetryJournals()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-08-13T02:00:00Z");
        var firstTouch = CreateTouch("touch-1", "down", createdUtc.AddSeconds(1), 10, 20);
        var secondTouch = CreateTouch("touch-2", "up", createdUtc.AddSeconds(2), 12, 22);
        var firstMetric = new SessionMetricSample
        {
            ChannelId = 1,
            Value = 60,
            CapturedAtUtc = createdUtc.AddSeconds(1),
            SegmentId = 1
        };
        var secondMetric = new SessionMetricSample
        {
            ChannelId = 1,
            Value = 58,
            CapturedAtUtc = createdUtc.AddSeconds(2),
            SegmentId = 2
        };

        AppSessionSnapshot CreateSnapshot(
            IReadOnlyList<SessionTouchInputRecord> touches,
            IReadOnlyList<SessionMetricSample> metrics,
            int totalMetricSampleCount,
            string status = "Connected") => new()
        {
            SessionId = "session-stale-live-payloads",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = status,
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = false,
            Touches = touches,
            MetricChannels =
            [
                new SessionMetricChannel
                {
                    ChannelId = 1,
                    Name = "FPS",
                    ColorHex = "#00FF00"
                }
            ],
            Metrics = metrics,
            TotalMetricSampleCount = totalMetricSampleCount
        };

        store.Save(CreateSnapshot([firstTouch, secondTouch], [firstMetric, secondMetric], 2));
        var sessionDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-stale-live-payloads");
        var touchesAppendFilePath = Path.Combine(sessionDirectoryPath, "touches.jsonl");
        var telemetryAppendFilePath = Path.Combine(sessionDirectoryPath, "telemetry", "segments.jsonl");
        var touchesLength = new FileInfo(touchesAppendFilePath).Length;
        var telemetryLength = new FileInfo(telemetryAppendFilePath).Length;

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        restartedStore.Save(CreateSnapshot([firstTouch], [firstMetric], 1));

        Assert.Equal(touchesLength, new FileInfo(touchesAppendFilePath).Length);
        Assert.Equal(telemetryLength, new FileInfo(telemetryAppendFilePath).Length);
        Assert.False(File.Exists(Path.Combine(sessionDirectoryPath, "touches.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDirectoryPath, "telemetry"), "channel-*.json"));
        Assert.True(restartedStore.TryLoad("session-stale-live-payloads", out var loadedSnapshot));
        Assert.Equal(2, loadedSnapshot!.Touches.Count);
        Assert.Equal(2, loadedSnapshot.Metrics.Count);
        Assert.Equal(2, loadedSnapshot.TotalMetricSampleCount);

        restartedStore.Save(CreateSnapshot(
            [firstTouch],
            [firstMetric],
            totalMetricSampleCount: 1,
            status: "WebSocket Closed"));

        Assert.False(File.Exists(touchesAppendFilePath));
        Assert.False(File.Exists(telemetryAppendFilePath));
        Assert.True(restartedStore.TryLoad("session-stale-live-payloads", out var closedSnapshot));
        Assert.Equal(2, closedSnapshot!.Touches.Count);
        Assert.Equal(2, closedSnapshot.Metrics.Count);
    }

    [Fact]
    public void Save_RecordingTelemetryTail_AppendsOnlyUnpersistedSamples()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-08-13T03:00:00Z");
        var metrics = Enumerable.Range(1, 4)
            .Select(index => new SessionMetricSample
            {
                ChannelId = 1,
                Value = 60 - index,
                CapturedAtUtc = createdUtc.AddSeconds(index),
                SegmentId = index
            })
            .ToArray();

        AppSessionSnapshot CreateSnapshot(
            IReadOnlyList<SessionMetricSample> retainedMetrics,
            int totalMetricSampleCount) => new()
        {
            SessionId = "session-live-telemetry-tail",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(totalMetricSampleCount),
            IsHistorical = false,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = retainedMetrics,
            TotalMetricSampleCount = totalMetricSampleCount
        };

        store.Save(CreateSnapshot(metrics[..3], 3));
        store.Save(CreateSnapshot(metrics[2..], 4));

        var telemetryAppendFilePath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-telemetry-tail",
            "telemetry",
            "segments.jsonl");
        Assert.Equal(2, File.ReadLines(telemetryAppendFilePath).Count());
        Assert.True(store.TryLoad("session-live-telemetry-tail", out var loadedSnapshot));
        Assert.Equal(metrics.Select(metric => metric.Value), loadedSnapshot!.Metrics.Select(metric => metric.Value));
        Assert.Equal(4, loadedSnapshot.TotalMetricSampleCount);
    }

    [Fact]
    public void SaveLiveSnapshot_PersistsSameCountVisualTreeReplacement()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var capturedAtUtc = createdUtc.AddSeconds(1);

        AppSessionSnapshot CreateSnapshot(int nodeCount) => new()
        {
            SessionId = "session-live-visual-tree-replacement",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = capturedAtUtc,
            IsHistorical = false,
            VisualTreeSnapshots =
            [
                new SessionVisualTreeSnapshot
                {
                    SnapshotId = "visual-tree-001",
                    CapturedAtUtc = capturedAtUtc,
                    Source = "test",
                    NodeCount = nodeCount,
                    Payload = new JsonObject
                    {
                        ["nodeCount"] = nodeCount
                    }
                }
            ],
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(CreateSnapshot(1));
        store.Save(CreateSnapshot(2));

        Assert.True(store.TryLoad("session-live-visual-tree-replacement", out var loadedSnapshot));
        var visualTree = Assert.Single(loadedSnapshot!.VisualTreeSnapshots);
        Assert.Equal(2, visualTree.NodeCount);
        Assert.Equal(2, visualTree.Payload["nodeCount"]?.GetValue<int>());
    }

    [Fact]
    public void SaveLiveSnapshot_AppendsVisualTreeDocumentsWithoutRewritingPersistedHistory()
    {
        using var environment = new TestSupport.TestEnvironment();
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var firstSnapshot = CreateVisualTreeSnapshot("visual-tree-001", createdUtc.AddSeconds(1), 1);
        var secondSnapshot = CreateVisualTreeSnapshot("visual-tree-002", createdUtc.AddSeconds(2), 2);
        var thirdSnapshot = CreateVisualTreeSnapshot("visual-tree-003", createdUtc.AddSeconds(3), 3);

        AppSessionSnapshot CreateSnapshot(IReadOnlyList<SessionVisualTreeSnapshot> visualTrees) => new()
        {
            SessionId = "session-live-visual-tree-append",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = visualTrees[^1].CapturedAtUtc,
            IsHistorical = false,
            VisualTreeSnapshots = visualTrees,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        var visualTreesDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-visual-tree-append",
            "visual-trees");
        var originalWriteTimeUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var restartedWriteTimeUtc = originalWriteTimeUtc.AddDays(1);

        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(CreateSnapshot([firstSnapshot]));
        var firstFilePath = Assert.Single(Directory.GetFiles(visualTreesDirectoryPath, "*.json"));
        File.SetLastWriteTimeUtc(firstFilePath, originalWriteTimeUtc);

        store.Save(CreateSnapshot([firstSnapshot, secondSnapshot]));

        Assert.Equal(originalWriteTimeUtc, File.GetLastWriteTimeUtc(firstFilePath));
        var firstTwoFilePaths = Directory.GetFiles(visualTreesDirectoryPath, "*.json");
        Assert.Equal(2, firstTwoFilePaths.Length);
        foreach (var filePath in firstTwoFilePaths)
        {
            File.SetLastWriteTimeUtc(filePath, restartedWriteTimeUtc);
        }

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        restartedStore.Save(CreateSnapshot(
        [
            CreateVisualTreeSnapshot("visual-tree-001", createdUtc.AddSeconds(1), 1),
            CreateVisualTreeSnapshot("visual-tree-002", createdUtc.AddSeconds(2), 2),
            thirdSnapshot
        ]));

        Assert.Equal(3, Directory.GetFiles(visualTreesDirectoryPath, "*.json").Length);
        Assert.All(firstTwoFilePaths, filePath => Assert.Equal(restartedWriteTimeUtc, File.GetLastWriteTimeUtc(filePath)));
    }

    [Fact]
    public void SaveLiveSnapshot_PersistsVisualTreesWithTheSameCaptureTimestamp()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var capturedAtUtc = createdUtc.AddSeconds(1);
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-live-visual-tree-same-timestamp",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = capturedAtUtc,
            IsHistorical = false,
            VisualTreeSnapshots =
            [
                CreateVisualTreeSnapshot("visual-tree-native", capturedAtUtc, 10),
                CreateVisualTreeSnapshot("visual-tree-maui", capturedAtUtc, 5)
            ],
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(snapshot);

        var visualTreesDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            snapshot.AppId,
            snapshot.SessionId,
            "visual-trees");
        Assert.Equal(2, Directory.GetFiles(visualTreesDirectoryPath, "*.json").Length);
        Assert.True(store.TryLoad(snapshot.SessionId, out var loadedSnapshot));
        Assert.Equal(
            ["visual-tree-maui", "visual-tree-native"],
            loadedSnapshot!.VisualTreeSnapshots.Select(tree => tree.SnapshotId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SaveLiveSnapshot_RemovesDeletedVisualTreeWithoutRewritingRetainedTree()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var firstSnapshot = CreateVisualTreeSnapshot("visual-tree-001", createdUtc.AddSeconds(1), 1);
        var retainedSnapshot = CreateVisualTreeSnapshot("visual-tree-002", createdUtc.AddSeconds(2), 2);

        AppSessionSnapshot CreateSnapshot(IReadOnlyList<SessionVisualTreeSnapshot> visualTrees) => new()
        {
            SessionId = "session-live-visual-tree-remove",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(3),
            IsHistorical = false,
            VisualTreeSnapshots = visualTrees,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(CreateSnapshot([firstSnapshot, retainedSnapshot]));
        var visualTreesDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-visual-tree-remove",
            "visual-trees");
        var firstFilePath = Path.Combine(visualTreesDirectoryPath, "2026-03-20T00-00-01.0000000Z.json");
        var retainedFilePath = Path.Combine(visualTreesDirectoryPath, "2026-03-20T00-00-02.0000000Z.json");
        var retainedWriteTimeUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(retainedFilePath, retainedWriteTimeUtc);

        store.Save(CreateSnapshot([retainedSnapshot]));

        Assert.False(File.Exists(firstFilePath));
        Assert.Equal(retainedWriteTimeUtc, File.GetLastWriteTimeUtc(retainedFilePath));
        Assert.True(store.TryLoad("session-live-visual-tree-remove", out var loadedSnapshot));
        Assert.Equal(retainedSnapshot.SnapshotId, Assert.Single(loadedSnapshot!.VisualTreeSnapshots).SnapshotId);
    }

    [Fact]
    public void SaveLiveSnapshot_AppendsArtifactDocumentsWithoutRewritingPersistedHistory()
    {
        using var environment = new TestSupport.TestEnvironment();
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var firstArtifact = CreateArtifactSnapshot("artifact-001", createdUtc.AddSeconds(1));
        var secondArtifact = CreateArtifactSnapshot("artifact-002", createdUtc.AddSeconds(2));

        AppSessionSnapshot CreateSnapshot(IReadOnlyList<SessionArtifactSnapshot> artifacts) => new()
        {
            SessionId = "session-live-artifact-append",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(2),
            IsHistorical = false,
            ArtifactSnapshots = artifacts,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        var store = new SessionCaptureStore(environment.ApplicationPaths);
        store.Save(CreateSnapshot([firstArtifact]));
        var artifactsDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-live-artifact-append",
            "artifacts");
        var firstFilePath = Assert.Single(Directory.GetFiles(artifactsDirectoryPath, "*.json"));
        var originalWriteTimeUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(firstFilePath, originalWriteTimeUtc);

        var restartedStore = new SessionCaptureStore(environment.ApplicationPaths);
        restartedStore.Save(CreateSnapshot(
        [
            CreateArtifactSnapshot("artifact-001", createdUtc.AddSeconds(1)),
            secondArtifact
        ]));

        Assert.Equal(2, Directory.GetFiles(artifactsDirectoryPath, "*.json").Length);
        Assert.Equal(originalWriteTimeUtc, File.GetLastWriteTimeUtc(firstFilePath));
    }

    [Fact]
    public void SaveLiveSnapshot_PersistsSameCountTelemetryReplacement()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var channel = new SessionMetricChannel
        {
            ChannelId = 1,
            Name = "FPS",
            ColorHex = "#00FF00"
        };

        AppSessionSnapshot CreateSnapshot(long value) => new()
        {
            SessionId = "session-live-telemetry-replacement",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "Connected",
            LastUpdatedUtc = createdUtc.AddSeconds(1),
            IsHistorical = false,
            MetricChannels = [channel],
            Metrics =
            [
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = value,
                    CapturedAtUtc = createdUtc.AddSeconds(1)
                }
            ]
        };

        store.Save(CreateSnapshot(60));
        store.Save(CreateSnapshot(45));

        Assert.True(store.TryLoad("session-live-telemetry-replacement", out var loadedSnapshot));
        Assert.Equal(45, Assert.Single(loadedSnapshot!.Metrics).Value);
    }
}
