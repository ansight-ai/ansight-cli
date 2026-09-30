using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class DeadTouchAnalyzer
{
    private const int MaximumReturnedFailureSignals = 20;

    private static readonly string[] InteractionTerms =
    [
        "gesture",
        "touch",
        "recognizer",
        "interaction",
        "input"
    ];

    private static readonly string[] FailureTerms =
    [
        "timeout",
        "timed out",
        "blocked",
        "failed",
        "cancelled",
        "canceled",
        "rejected",
        "prevented"
    ];

    public static DeadTouchCandidate BuildDeadTouchCandidate(
        AppSessionSnapshot snapshot,
        TouchGestureSegment gesture,
        TimeSpan responseWindow,
        bool includeScreenshotsAsEvidence)
    {
        var responseStartUtc = gesture.EndUtc;
        var responseEndUtc = responseStartUtc.Add(responseWindow);
        var responseLogs = snapshot.Logs
            .Where(log => IsInRange(log.TimestampUtc, responseStartUtc, responseEndUtc))
            .ToArray();
        var failureSignals = snapshot.Logs
            .Where(log => IsInRange(log.TimestampUtc, gesture.StartUtc, responseEndUtc))
            .Where(IsGestureFailureSignal)
            .ToArray();
        var responseLogsWithoutFailureSignals = responseLogs.Count(log => !IsGestureFailureSignal(log));
        var screenshots = CountInRange(snapshot.Images.Select(image => image.CapturedAtUtc), responseStartUtc, responseEndUtc);
        var visualTrees = CountInRange(snapshot.VisualTreeSnapshots.Select(visualTree => visualTree.CapturedAtUtc), responseStartUtc, responseEndUtc);
        var artifactSnapshots = CountInRange(snapshot.ArtifactSnapshots.Select(artifact => artifact.CapturedAtUtc), responseStartUtc, responseEndUtc);
        var annotations = snapshot.Annotations.Count(annotation => TouchReviewGeometry.AnnotationOverlaps(annotation, responseStartUtc, responseEndUtc));
        var evidenceCount = responseLogsWithoutFailureSignals + visualTrees + artifactSnapshots + annotations + (includeScreenshotsAsEvidence ? screenshots : 0);
        var isDead = failureSignals.Length > 0 || evidenceCount == 0;
        var payload = new JsonObject
        {
            ["gesture"] = TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: false),
            ["responseWindowStartUtc"] = responseStartUtc,
            ["responseWindowEndUtc"] = responseEndUtc,
            ["classification"] = failureSignals.Length > 0 ? "gestureFailure" : "noResponse",
            ["failureSignalCount"] = failureSignals.Length,
            ["failureSignals"] = PayloadJson.CreateJsonArray(
                failureSignals
                    .Take(MaximumReturnedFailureSignals)
                    .Select(log => (JsonNode?)BuildFailureSignalPayload(log))),
            ["evidenceCounts"] = new JsonObject
            {
                ["logs"] = responseLogs.Length,
                ["responseLogs"] = responseLogsWithoutFailureSignals,
                ["failureSignals"] = failureSignals.Length,
                ["screenshots"] = screenshots,
                ["visualTrees"] = visualTrees,
                ["artifactSnapshots"] = artifactSnapshots,
                ["annotations"] = annotations
            }
        };

        return new DeadTouchCandidate(isDead, payload);
    }

    private static bool IsGestureFailureSignal(LogEntry log)
    {
        var message = log.Message;
        return InteractionTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase))
               && FailureTerms.Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject BuildFailureSignalPayload(LogEntry log)
    {
        return new JsonObject
        {
            ["timestampUtc"] = log.TimestampUtc,
            ["priority"] = log.Priority.ToString(),
            ["streamId"] = log.StreamId,
            ["source"] = log.Source,
            ["tag"] = log.Tag,
            ["eventId"] = log.EventId,
            ["message"] = log.Message
        };
    }

    private static int CountInRange(IEnumerable<DateTimeOffset> timestamps, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        return timestamps.Count(timestamp => IsInRange(timestamp, startUtc, endUtc));
    }

    private static bool IsInRange(DateTimeOffset timestampUtc, DateTimeOffset startUtc, DateTimeOffset endUtc)
        => timestampUtc >= startUtc && timestampUtc <= endUtc;
}
