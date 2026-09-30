using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed partial class RuntimeStateTests
{
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

    private static SessionTouchInputRecord CreateTouch(string id, DateTimeOffset capturedAtUtc)
    {
        return new SessionTouchInputRecord
        {
            Id = id,
            Action = "pressed",
            CapturedAtUtc = capturedAtUtc,
            PointerId = 1,
            PointerIndex = 0,
            PointerCount = 1,
            X = 160,
            Y = 100,
            NormalizedX = 0.5,
            NormalizedY = 0.5,
            SurfaceWidth = 320,
            SurfaceHeight = 200,
            CoordinateUnit = "pixels"
        };
    }

    private static SessionImageFrame CreateImageFrame(string frameId, DateTimeOffset capturedAtUtc)
    {
        return new SessionImageFrame
        {
            FrameId = frameId,
            CapturedAtUtc = capturedAtUtc,
            Format = "jpeg",
            Width = 320,
            Height = 200,
            Quality = 80,
            ByteCount = 3
        };
    }

    private static SessionVisualTreeSnapshot CreateVisualTreeSnapshot(string snapshotId, DateTimeOffset capturedAtUtc)
    {
        return new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "Replay",
            NodeCount = 1,
            Payload = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "root"
            }
        };
    }

    private static SessionArtifactSnapshot CreateArtifactSnapshot(string snapshotId, DateTimeOffset capturedAtUtc)
    {
        return new SessionArtifactSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            Source = "Replay",
            RootAlias = "AppData",
            RelativePath = ".",
            Name = snapshotId,
            Kind = "directory",
            ArtifactDirectoryName = snapshotId
        };
    }

    private static int GetLoadedSessionStateCount(RuntimeState runtimeState)
    {
        var field = typeof(RuntimeState).GetField(
            "sessionsById",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(runtimeState);
        Assert.NotNull(value);
        var countProperty = value!.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);
        return Assert.IsType<int>(countProperty!.GetValue(value));
    }

    private static SessionAnnotationGeometry CreateAnnotationGeometry(
        string geometryId,
        string frameId,
        DateTimeOffset capturedAtUtc)
    {
        return new SessionAnnotationGeometry
        {
            GeometryId = geometryId,
            FrameId = frameId,
            CapturedAtUtc = capturedAtUtc,
            Kind = SessionAnnotationGeometryKind.Point,
            X = 0.5,
            Y = 0.5
        };
    }
}
