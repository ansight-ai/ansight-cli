using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DeadTouchAnalyzerTests
{
    [Theory]
    [InlineData("Gesture-gate timeout waiting for recognizer")]
    [InlineData("Blocked-recognizer prevented interaction")]
    [InlineData("Touch input was cancelled by the active recognizer")]
    public void BuildDeadTouchCandidate_ClassifiesGestureFailureLogsAsDead(string message)
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-10T01:02:03Z");
        var gesture = CreateGesture(startedAtUtc);
        var snapshot = CreateSnapshot(
            new LogEntry(startedAtUtc.AddMilliseconds(150), message)
            {
                Priority = LogPriority.Warning,
                Source = "UIKit"
            });

        var candidate = DeadTouchAnalyzer.BuildDeadTouchCandidate(
            snapshot,
            gesture,
            TimeSpan.FromMilliseconds(500),
            includeScreenshotsAsEvidence: false);

        Assert.True(candidate.IsDead);
        Assert.Equal("gestureFailure", candidate.Payload["classification"]?.GetValue<string>());
        Assert.Equal(1, candidate.Payload["failureSignalCount"]?.GetValue<int>());
        Assert.Equal(1, candidate.Payload["failureSignals"]?.AsArray().Count);
        Assert.Equal(message, candidate.Payload["failureSignals"]?[0]?["message"]?.GetValue<string>());
    }

    [Fact]
    public void BuildDeadTouchCandidate_DoesNotTreatFailureLogAsSuccessfulResponseEvidence()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-10T01:02:03Z");
        var gesture = CreateGesture(startedAtUtc);
        var snapshot = CreateSnapshot(
            new LogEntry(startedAtUtc.AddMilliseconds(200), "Gesture recognizer blocked the requested tap")
            {
                Priority = LogPriority.Error
            });

        var candidate = DeadTouchAnalyzer.BuildDeadTouchCandidate(
            snapshot,
            gesture,
            TimeSpan.FromMilliseconds(500),
            includeScreenshotsAsEvidence: false);

        var counts = candidate.Payload["evidenceCounts"]?.AsObject();
        Assert.True(candidate.IsDead);
        Assert.Equal(1, counts?["logs"]?.GetValue<int>());
        Assert.Equal(0, counts?["responseLogs"]?.GetValue<int>());
        Assert.Equal(1, counts?["failureSignals"]?.GetValue<int>());
    }

    [Fact]
    public void BuildDeadTouchCandidate_KeepsOrdinaryResponseLogsAsEvidence()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-10T01:02:03Z");
        var gesture = CreateGesture(startedAtUtc);
        var snapshot = CreateSnapshot(
            new LogEntry(startedAtUtc.AddMilliseconds(200), "Navigation completed")
            {
                Priority = LogPriority.Information
            });

        var candidate = DeadTouchAnalyzer.BuildDeadTouchCandidate(
            snapshot,
            gesture,
            TimeSpan.FromMilliseconds(500),
            includeScreenshotsAsEvidence: false);

        Assert.False(candidate.IsDead);
        Assert.Equal(0, candidate.Payload["failureSignalCount"]?.GetValue<int>());
    }

    private static TouchGestureSegment CreateGesture(DateTimeOffset startedAtUtc)
    {
        var completedAtUtc = startedAtUtc.AddMilliseconds(100);
        var touches = new[]
        {
            CreateTouch("touch-down", "down", startedAtUtc),
            CreateTouch("touch-up", "up", completedAtUtc)
        };
        return new TouchGestureSegment(
            "gesture-001",
            "tap",
            [1],
            touches,
            startedAtUtc,
            completedAtUtc,
            100,
            0,
            0,
            true);
    }

    private static SessionTouchInputRecord CreateTouch(string id, string action, DateTimeOffset capturedAtUtc)
    {
        return new SessionTouchInputRecord
        {
            Id = id,
            Action = action,
            CapturedAtUtc = capturedAtUtc,
            PointerId = 1,
            PointerIndex = 0,
            PointerCount = 1,
            X = 50,
            Y = 50,
            NormalizedX = 0.5,
            NormalizedY = 0.5,
            SurfaceWidth = 100,
            SurfaceHeight = 100,
            CoordinateUnit = "pixels"
        };
    }

    private static AppSessionSnapshot CreateSnapshot(params LogEntry[] logs)
    {
        var createdAtUtc = DateTimeOffset.Parse("2026-08-10T01:00:00Z");
        return new AppSessionSnapshot
        {
            SessionId = "session-001",
            AppId = "com.example.app",
            ClientName = "Test App",
            RemoteAddress = "loopback",
            CreatedUtc = createdAtUtc,
            ConfigId = null,
            Status = "Complete",
            LastUpdatedUtc = createdAtUtc,
            IsHistorical = true,
            Logs = logs,
            MetricChannels = Array.Empty<SessionMetricChannel>(),
            Metrics = Array.Empty<SessionMetricSample>()
        };
    }
}
