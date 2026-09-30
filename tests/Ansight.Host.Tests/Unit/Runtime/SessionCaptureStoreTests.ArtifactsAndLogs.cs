using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    [Fact]
    public async Task SaveSessionImageAsync_IgnoresDuplicateBytes()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);

        var firstFrame = await store.SaveSessionImageAsync(
            "com.example.testapp",
            "session-001",
            DateTimeOffset.Parse("2026-03-20T00:00:00Z"),
            "jpeg",
            320,
            200,
            80,
            new byte[] { 1, 2, 3, 4, 5 });

        var secondFrame = await store.SaveSessionImageAsync(
            "com.example.testapp",
            "session-001",
            DateTimeOffset.Parse("2026-03-20T00:00:01Z"),
            "jpeg",
            320,
            200,
            80,
            new byte[] { 1, 2, 3, 4, 5 });

        Assert.NotNull(firstFrame);
        Assert.Null(secondFrame);
        var firstImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-001",
            firstFrame!);
        Assert.True(File.Exists(firstImagePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(firstImagePath)!, "*.jpg"));
    }

    [Fact]
    public void TryLoad_ExcludesImageEntriesWithoutPersistedArtifacts()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var persistedFrame = new SessionImageFrame
        {
            FrameId = "frame-001",
            CapturedAtUtc = createdUtc.AddSeconds(1),
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 5
        };
        var missingFrame = new SessionImageFrame
        {
            FrameId = "frame-002",
            CapturedAtUtc = createdUtc.AddSeconds(2),
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 5
        };
        var sessionCapturesRootPath = SessionImageArtifactPath.ResolveSessionCapturesRootPath(environment.ApplicationPaths);
        var persistedImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
            sessionCapturesRootPath,
            "com.example.testapp",
            "session-images-filter",
            persistedFrame);
        Directory.CreateDirectory(Path.GetDirectoryName(persistedImagePath)!);
        File.WriteAllBytes(persistedImagePath, [1, 2, 3, 4, 5]);

        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-images-filter",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "WebSocket Closed",
            LastUpdatedUtc = createdUtc.AddMinutes(1),
            IsHistorical = false,
            Images = [persistedFrame, missingFrame],
            Logs = Array.Empty<LogEntry>(),
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };

        store.Save(snapshot);

        var loadedSuccessfully = store.TryLoad(snapshot.SessionId, out var loadedSnapshot);

        Assert.True(loadedSuccessfully);
        Assert.NotNull(loadedSnapshot);
        var loadedFrame = Assert.Single(loadedSnapshot!.Images);
        Assert.Equal(persistedFrame.FrameId, loadedFrame.FrameId);
    }

    [Fact]
    public void TryLoad_IgnoresNonCanonicalTelemetryBlobCopies()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-03-20T00:00:00Z");
        var snapshot = new AppSessionSnapshot
        {
            SessionId = "session-dup-telemetry",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = "WebSocket Closed",
            LastUpdatedUtc = createdUtc.AddMinutes(1),
            IsHistorical = false,
            Logs = Array.Empty<LogEntry>(),
            MetricChannels =
            [
                new SessionMetricChannel
                {
                    ChannelId = 1,
                    Name = "Native heap",
                    ColorHex = "#007AFF"
                }
            ],
            Metrics =
            [
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = 123,
                    CapturedAtUtc = createdUtc.AddSeconds(1)
                },
                new SessionMetricSample
                {
                    ChannelId = 1,
                    Value = 124,
                    CapturedAtUtc = createdUtc.AddSeconds(2)
                }
            ]
        };

        store.Save(snapshot);

        var telemetryDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-dup-telemetry",
            "telemetry");
        var canonicalTelemetryFilePath = Path.Combine(telemetryDirectoryPath, "channel-001.json");
        var duplicateTelemetryFilePath = Path.Combine(telemetryDirectoryPath, "channel-001 2.json");
        File.Copy(canonicalTelemetryFilePath, duplicateTelemetryFilePath);

        var loadedSuccessfully = store.TryLoad(snapshot.SessionId, out var loadedSnapshot);

        Assert.True(loadedSuccessfully);
        Assert.NotNull(loadedSnapshot);
        Assert.Equal(2, loadedSnapshot!.Metrics.Count);
    }

    [Fact]
    public void NativeLogBatches_CompactCompleteHistoryWhenLiveSnapshotOnlyRetainsTail()
    {
        using var environment = new TestSupport.TestEnvironment();
        var store = new SessionCaptureStore(environment.ApplicationPaths);
        var createdUtc = DateTimeOffset.Parse("2026-07-14T03:04:05Z");
        var sdkEntries = new[]
        {
            CreateLogEntry(createdUtc.AddSeconds(1), SessionLogStreamIds.AnsightSdk, "sdk-1"),
            CreateLogEntry(createdUtc.AddSeconds(2), SessionLogStreamIds.AnsightSdk, "sdk-2")
        };
        var nativeEntries = new[]
        {
            CreateLogEntry(createdUtc.AddSeconds(3), SessionLogStreamIds.AppleUnifiedLog, "native-1"),
            CreateLogEntry(createdUtc.AddSeconds(4), SessionLogStreamIds.AppleUnifiedLog, "native-2")
        };

        store.Save(CreateLogPersistenceSnapshot(createdUtc, "Connected", [], 0, 0));
        store.QueueLogBatch(new SessionLogBatchEventArgs(
            "session-log-tail",
            "com.example.testapp",
            SessionLogStreamIds.AnsightSdk,
            sdkEntries,
            streamEntryCount: 2,
            totalEntryCount: 2));
        store.Save(CreateLogPersistenceSnapshot(createdUtc, "Connected", sdkEntries, 2, 0));
        store.QueueLogBatch(new SessionLogBatchEventArgs(
            "session-log-tail",
            "com.example.testapp",
            SessionLogStreamIds.AppleUnifiedLog,
            nativeEntries,
            streamEntryCount: 2,
            totalEntryCount: 4));
        store.Save(CreateLogPersistenceSnapshot(
            createdUtc,
            "WebSocket Closed",
            nativeEntries,
            totalLogCount: 4,
            retainedLogStartIndex: 2));

        var loadedSuccessfully = store.TryLoad("session-log-tail", out var loadedSnapshot);

        Assert.True(loadedSuccessfully);
        Assert.NotNull(loadedSnapshot);
        Assert.Equal(4, loadedSnapshot!.TotalLogCount);
        Assert.Equal(
            ["sdk-1", "sdk-2", "native-1", "native-2"],
            loadedSnapshot.Logs.Select(entry => entry.Message));
        Assert.Equal(2, loadedSnapshot.LogStreams.Single(stream => stream.StreamId == SessionLogStreamIds.AnsightSdk).Entries.Count);
        Assert.Equal(2, loadedSnapshot.LogStreams.Single(stream => stream.StreamId == SessionLogStreamIds.AppleUnifiedLog).Entries.Count);

        var logSegmentsDirectoryPath = Path.Combine(
            environment.ApplicationPaths.ApplicationDataPath,
            "session-captures",
            "com.example.testapp",
            "session-log-tail",
            "log-segments");
        Assert.False(Directory.Exists(logSegmentsDirectoryPath));
    }
}
