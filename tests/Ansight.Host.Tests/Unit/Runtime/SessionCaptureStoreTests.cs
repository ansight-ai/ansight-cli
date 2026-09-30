using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class SessionCaptureStoreTests
{
    private static LogEntry CreateLogEntry(DateTimeOffset timestampUtc, string streamId, string message)
        => new(timestampUtc, message)
        {
            StreamId = streamId,
            Source = "Test"
        };

    private static AppSessionSnapshot CreateLogPersistenceSnapshot(
        DateTimeOffset createdUtc,
        string status,
        IReadOnlyList<LogEntry> retainedLogs,
        int totalLogCount,
        int retainedLogStartIndex)
    {
        var streams = new[]
        {
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AnsightSdk,
                Kind = SessionLogStreamKinds.AnsightSdk,
                DisplayName = "Ansight SDK",
                Entries = retainedLogs.Where(entry => entry.StreamId == SessionLogStreamIds.AnsightSdk).ToArray(),
                TotalEntryCount = Math.Min(2, totalLogCount)
            },
            new SessionLogStream
            {
                StreamId = SessionLogStreamIds.AppleUnifiedLog,
                Kind = SessionLogStreamKinds.AppleUnifiedLog,
                DisplayName = "Apple Unified Log",
                Entries = retainedLogs.Where(entry => entry.StreamId == SessionLogStreamIds.AppleUnifiedLog).ToArray(),
                TotalEntryCount = Math.Max(0, totalLogCount - 2)
            }
        };
        return new AppSessionSnapshot
        {
            SessionId = "session-log-tail",
            AppId = "com.example.testapp",
            ClientName = "Integration Client",
            RemoteAddress = "127.0.0.1",
            CreatedUtc = createdUtc,
            ConfigId = "config-001",
            Status = status,
            LastUpdatedUtc = createdUtc.AddMinutes(1),
            IsHistorical = false,
            LogStreams = streams,
            Logs = retainedLogs,
            TotalLogCount = totalLogCount,
            RetainedLogStartIndex = retainedLogStartIndex,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }

    private static SessionTouchInputRecord CreateTouch(
        string id,
        string action,
        DateTimeOffset capturedAtUtc,
        double x,
        double y)
    {
        return new SessionTouchInputRecord
        {
            Id = id,
            Action = action,
            CapturedAtUtc = capturedAtUtc,
            PointerId = 0,
            PointerIndex = 0,
            PointerCount = 1,
            X = x,
            Y = y,
            SurfaceWidth = 100,
            SurfaceHeight = 200,
            CoordinateSpace = "window",
            CoordinateUnit = "px",
            SurfaceScale = 1
        };
    }

    private static SessionVisualTreeSnapshot CreateVisualTreeSnapshot(
        string snapshotId,
        DateTimeOffset capturedAtUtc,
        int nodeCount)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "test",
            NodeCount = nodeCount,
            Payload = new JsonObject
            {
                ["nodeCount"] = nodeCount
            }
        };
    }

    private static SessionArtifactSnapshot CreateArtifactSnapshot(string snapshotId, DateTimeOffset capturedAtUtc)
    {
        return new SessionArtifactSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "test",
            RootAlias = "appData",
            RelativePath = string.Empty,
            Name = "App data",
            Kind = "directory",
            ArtifactDirectoryName = snapshotId,
            Entries = Array.Empty<SessionArtifactEntry>()
        };
    }
}
