using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal static class TouchArtifactPayloadBuilder
{
    public static JsonObject BuildTouchArtifactsPayload(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        TimeSpan window,
        int limitPerType)
    {
        var startUtc = targetStartUtc <= targetEndUtc ? targetStartUtc : targetEndUtc;
        var endUtc = targetEndUtc >= targetStartUtc ? targetEndUtc : targetStartUtc;
        var rangeStartUtc = startUtc.Subtract(window);
        var rangeEndUtc = endUtc.Add(window);
        return new JsonObject
        {
            ["rangeStartUtc"] = rangeStartUtc,
            ["rangeEndUtc"] = rangeEndUtc,
            ["logs"] = BuildNearestLogsArray(snapshot, startUtc, endUtc, rangeStartUtc, rangeEndUtc, limitPerType),
            ["screenshots"] = BuildNearestScreenshotsArray(snapshot, startUtc, endUtc, rangeStartUtc, rangeEndUtc, limitPerType),
            ["annotations"] = BuildNearestAnnotationsArray(snapshot, startUtc, endUtc, rangeStartUtc, rangeEndUtc, limitPerType),
            ["visualTrees"] = BuildNearestVisualTreesArray(snapshot, startUtc, endUtc, rangeStartUtc, rangeEndUtc, limitPerType),
            ["artifactSnapshots"] = BuildNearestArtifactSnapshotsArray(snapshot, startUtc, endUtc, rangeStartUtc, rangeEndUtc, limitPerType)
        };
    }

    public static JsonArray BuildLogsArray(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Logs
            .Where(log => PayloadJson.MatchesTimestamp(log.TimestampUtc, startUtc, endUtc))
            .OrderBy(log => log.TimestampUtc)
            .Take(limit)
            .Select(log => (JsonNode?)new JsonObject
            {
                ["timestampUtc"] = log.TimestampUtc,
                ["priority"] = log.Priority.ToString(),
                ["source"] = log.Source,
                ["tag"] = log.Tag,
                ["eventId"] = log.EventId,
                ["message"] = log.Message
            }));
    }

    public static JsonArray BuildScreenshotsArray(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Images
            .Where(image => PayloadJson.MatchesTimestamp(image.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(image => image.CapturedAtUtc)
            .Take(limit)
            .Select(image => (JsonNode?)BuildScreenshotPayload(image, null)));
    }

    public static JsonArray BuildAnnotationsArray(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Annotations
            .Where(annotation => TouchReviewGeometry.AnnotationOverlaps(annotation, startUtc, endUtc))
            .OrderBy(annotation => annotation.StartUtc)
            .ThenBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
            .Take(limit)
            .Select(annotation => (JsonNode?)PayloadJson.BuildSessionAnnotationPayload(annotation)));
    }

    public static JsonArray BuildVisualTreesArray(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.VisualTreeSnapshots
            .Where(visualTree => PayloadJson.MatchesTimestamp(visualTree.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(visualTree => visualTree.CapturedAtUtc)
            .ThenBy(visualTree => visualTree.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(visualTree => (JsonNode?)PayloadJson.BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, visualTree)));
    }

    public static JsonArray BuildArtifactSnapshotsArray(AppSessionSnapshot snapshot, DateTimeOffset startUtc, DateTimeOffset endUtc, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.ArtifactSnapshots
            .Where(artifact => PayloadJson.MatchesTimestamp(artifact.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(artifact => artifact.CapturedAtUtc)
            .ThenBy(artifact => artifact.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(artifact => (JsonNode?)BuildArtifactSnapshotPayload(artifact, null)));
    }

    private static JsonArray BuildNearestLogsArray(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Logs
            .Where(log => PayloadJson.MatchesTimestamp(log.TimestampUtc, rangeStartUtc, rangeEndUtc))
            .OrderBy(log => TouchReviewGeometry.CalculateDistanceMilliseconds(log.TimestampUtc, targetStartUtc, targetEndUtc))
            .ThenBy(log => log.TimestampUtc)
            .Take(limit)
            .Select(log => (JsonNode?)new JsonObject
            {
                ["timestampUtc"] = log.TimestampUtc,
                ["deltaMs"] = TouchReviewGeometry.CalculateDistanceMilliseconds(log.TimestampUtc, targetStartUtc, targetEndUtc),
                ["priority"] = log.Priority.ToString(),
                ["source"] = log.Source,
                ["tag"] = log.Tag,
                ["eventId"] = log.EventId,
                ["message"] = log.Message
            }));
    }

    private static JsonArray BuildNearestScreenshotsArray(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Images
            .Where(image => PayloadJson.MatchesTimestamp(image.CapturedAtUtc, rangeStartUtc, rangeEndUtc))
            .OrderBy(image => TouchReviewGeometry.CalculateDistanceMilliseconds(image.CapturedAtUtc, targetStartUtc, targetEndUtc))
            .ThenBy(image => image.CapturedAtUtc)
            .Take(limit)
            .Select(image => (JsonNode?)BuildScreenshotPayload(image, TouchReviewGeometry.CalculateDistanceMilliseconds(image.CapturedAtUtc, targetStartUtc, targetEndUtc))));
    }

    private static JsonArray BuildNearestAnnotationsArray(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Annotations
            .Where(annotation => TouchReviewGeometry.AnnotationOverlaps(annotation, rangeStartUtc, rangeEndUtc))
            .OrderBy(annotation => TouchReviewGeometry.CalculateDistanceMilliseconds(annotation.StartUtc, targetStartUtc, targetEndUtc))
            .ThenBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
            .Take(limit)
            .Select(annotation =>
            {
                var payload = PayloadJson.BuildSessionAnnotationPayload(annotation);
                payload["deltaMs"] = TouchReviewGeometry.CalculateDistanceMilliseconds(annotation.StartUtc, targetStartUtc, targetEndUtc);
                return (JsonNode?)payload;
            }));
    }

    private static JsonArray BuildNearestVisualTreesArray(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.VisualTreeSnapshots
            .Where(visualTree => PayloadJson.MatchesTimestamp(visualTree.CapturedAtUtc, rangeStartUtc, rangeEndUtc))
            .OrderBy(visualTree => TouchReviewGeometry.CalculateDistanceMilliseconds(visualTree.CapturedAtUtc, targetStartUtc, targetEndUtc))
            .ThenBy(visualTree => visualTree.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(visualTree =>
            {
                var payload = PayloadJson.BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, visualTree);
                payload["deltaMs"] = TouchReviewGeometry.CalculateDistanceMilliseconds(visualTree.CapturedAtUtc, targetStartUtc, targetEndUtc);
                return (JsonNode?)payload;
            }));
    }

    private static JsonArray BuildNearestArtifactSnapshotsArray(
        AppSessionSnapshot snapshot,
        DateTimeOffset targetStartUtc,
        DateTimeOffset targetEndUtc,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.ArtifactSnapshots
            .Where(artifact => PayloadJson.MatchesTimestamp(artifact.CapturedAtUtc, rangeStartUtc, rangeEndUtc))
            .OrderBy(artifact => TouchReviewGeometry.CalculateDistanceMilliseconds(artifact.CapturedAtUtc, targetStartUtc, targetEndUtc))
            .ThenBy(artifact => artifact.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(artifact => (JsonNode?)BuildArtifactSnapshotPayload(artifact, TouchReviewGeometry.CalculateDistanceMilliseconds(artifact.CapturedAtUtc, targetStartUtc, targetEndUtc))));
    }

    private static JsonObject BuildScreenshotPayload(SessionImageFrame image, long? deltaMs)
    {
        return new JsonObject
        {
            ["frameId"] = image.FrameId,
            ["capturedAtUtc"] = image.CapturedAtUtc,
            ["deltaMs"] = deltaMs,
            ["format"] = image.Format,
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["quality"] = image.Quality,
            ["byteCount"] = image.ByteCount
        };
    }

    private static JsonObject BuildArtifactSnapshotPayload(SessionArtifactSnapshot artifact, long? deltaMs)
    {
        return new JsonObject
        {
            ["snapshotId"] = artifact.SnapshotId,
            ["capturedAtUtc"] = artifact.CapturedAtUtc,
            ["deltaMs"] = deltaMs,
            ["source"] = artifact.Source,
            ["rootAlias"] = artifact.RootAlias,
            ["relativePath"] = artifact.RelativePath,
            ["name"] = artifact.Name,
            ["kind"] = artifact.Kind,
            ["directoryCount"] = artifact.DirectoryCount,
            ["fileCount"] = artifact.FileCount,
            ["byteCount"] = artifact.ByteCount,
            ["truncated"] = artifact.Truncated
        };
    }
}
