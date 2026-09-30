using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal sealed class GetNearestArtifactsTool : Operation
{
    public GetNearestArtifactsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_nearest_artifacts";

    protected override string Title => "Get Nearest Artifacts";

    protected override string Description => "Return logs, screenshots, visual trees, annotations, telemetry samples, and artifact snapshots nearest to a target timestamp or log entry.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["platform"] = SessionInspectionToolSchemas.PlatformSchema("Optional platform key filter."),
            ["platforms"] = SessionInspectionToolSchemas.PlatformsSchema(),
            ["timestampUtc"] = ToolSchema.String("Optional target timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["eventId"] = ToolSchema.String("Optional event id to select a target log.", nullable: true),
            ["logIndex"] = ToolSchema.Integer("Optional zero-based index in the session's timestamp-ordered log stream.", nullable: true),
            ["windowSeconds"] = ToolSchema.Integer("Seconds on either side of the target to search. Defaults to 30, max 3600.", nullable: true),
            ["limitPerType"] = ToolSchema.Integer("Maximum nearest entries per artifact type. Defaults to 3, max 20.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Unable to resolve session."));
        }

        var targetUtc = SessionReviewContext.ResolveTargetTimestamp(snapshot!, arguments, out var targetPayload, out errorMessage);
        if (!targetUtc.HasValue)
        {
            return Task.FromResult(ToolError(errorMessage ?? "Unable to resolve target timestamp."));
        }

        if (!ArgumentReader.TryReadPositiveLimit(
                arguments,
                "windowSeconds",
                OperationDefaults.DefaultNearestArtifactWindowSeconds,
                OperationDefaults.MaxNearestArtifactWindowSeconds,
                out var windowSeconds,
                out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limitPerType",
                OperationDefaults.DefaultNearestArtifactLimitPerType,
                OperationDefaults.MaxNearestArtifactLimitPerType,
                out var limitPerType,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid nearest-artifact arguments."));
        }

        var target = targetUtc.Value.ToUniversalTime();
        var window = TimeSpan.FromSeconds(windowSeconds);
        var liveSessionIds = sessionResolver.GetLiveSessionIds();
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, liveSessionIds.Contains(snapshot!.SessionId)),
                ["target"] = targetPayload,
                ["targetUtc"] = target,
                ["windowSeconds"] = windowSeconds,
                ["limitPerType"] = limitPerType,
                ["logs"] = BuildNearestLogs(snapshot, target, window, limitPerType),
                ["screenshots"] = BuildNearestScreenshots(snapshot, target, window, limitPerType),
                ["visualTrees"] = BuildNearestVisualTrees(snapshot, target, window, limitPerType),
                ["annotations"] = BuildNearestAnnotations(snapshot, target, window, limitPerType),
                ["telemetrySamples"] = BuildNearestTelemetrySamples(snapshot, target, window, limitPerType),
                ["artifactSnapshots"] = BuildNearestArtifactSnapshots(snapshot, target, window, limitPerType)
            },
            isError: false));
    }

    private static JsonArray BuildNearestLogs(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        return PayloadJson.CreateJsonArray(SessionReviewContext.BuildIndexedReviewLogs(snapshot)
            .Where(entry => IsInsideWindow(entry.Log.TimestampUtc, targetUtc, window))
            .OrderBy(entry => SessionReviewContext.CalculateDeltaMilliseconds(entry.Log.TimestampUtc, targetUtc))
            .ThenBy(entry => entry.Index)
            .Take(limit)
            .Select(entry => (JsonNode?)new JsonObject
            {
                ["logIndex"] = entry.Index,
                ["timestampUtc"] = entry.Log.TimestampUtc,
                ["deltaMilliseconds"] = SessionReviewContext.CalculateDeltaMilliseconds(entry.Log.TimestampUtc, targetUtc),
                ["priority"] = entry.Log.Priority.ToString(),
                ["source"] = entry.Log.Source,
                ["tag"] = entry.Log.Tag,
                ["eventId"] = entry.Log.EventId,
                ["message"] = entry.Log.Message
            }));
    }

    private static JsonArray BuildNearestScreenshots(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Images
            .Where(image => IsInsideWindow(image.CapturedAtUtc, targetUtc, window))
            .OrderBy(image => SessionReviewContext.CalculateDeltaMilliseconds(image.CapturedAtUtc, targetUtc))
            .ThenBy(image => image.FrameId, StringComparer.Ordinal)
            .Take(limit)
            .Select(image => (JsonNode?)new JsonObject
            {
                ["frameId"] = image.FrameId,
                ["capturedAtUtc"] = image.CapturedAtUtc,
                ["deltaMilliseconds"] = SessionReviewContext.CalculateDeltaMilliseconds(image.CapturedAtUtc, targetUtc),
                ["format"] = image.Format,
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["quality"] = image.Quality,
                ["byteCount"] = image.ByteCount
            }));
    }

    private static JsonArray BuildNearestVisualTrees(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.VisualTreeSnapshots
            .Where(tree => IsInsideWindow(tree.CapturedAtUtc, targetUtc, window))
            .OrderBy(tree => SessionReviewContext.CalculateDeltaMilliseconds(tree.CapturedAtUtc, targetUtc))
            .ThenBy(tree => tree.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(tree =>
            {
                var payload = PayloadJson.BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, tree);
                payload["deltaMilliseconds"] = SessionReviewContext.CalculateDeltaMilliseconds(tree.CapturedAtUtc, targetUtc);
                return (JsonNode?)payload;
            }));
    }

    private static JsonArray BuildNearestAnnotations(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.Annotations
            .Where(annotation => CalculateAnnotationDistance(annotation, targetUtc) <= window)
            .OrderBy(annotation => CalculateAnnotationDistance(annotation, targetUtc))
            .ThenBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
            .Take(limit)
            .Select(annotation =>
            {
                var payload = PayloadJson.BuildSessionAnnotationPayload(annotation);
                payload["deltaMilliseconds"] = (long)CalculateAnnotationDistance(annotation, targetUtc).TotalMilliseconds;
                payload["isActiveAtTarget"] = annotation.StartUtc <= targetUtc && (annotation.EndUtc ?? annotation.StartUtc) >= targetUtc;
                return (JsonNode?)payload;
            }));
    }

    private static JsonArray BuildNearestTelemetrySamples(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        var channelMap = snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
        return PayloadJson.CreateJsonArray(snapshot.Metrics
            .Where(sample => IsInsideWindow(sample.CapturedAtUtc, targetUtc, window))
            .OrderBy(sample => SessionReviewContext.CalculateDeltaMilliseconds(sample.CapturedAtUtc, targetUtc))
            .ThenBy(sample => sample.ChannelId)
            .Take(limit)
            .Select(sample =>
            {
                channelMap.TryGetValue(sample.ChannelId, out var channel);
                return (JsonNode?)new JsonObject
                {
                    ["capturedAtUtc"] = sample.CapturedAtUtc,
                    ["deltaMilliseconds"] = SessionReviewContext.CalculateDeltaMilliseconds(sample.CapturedAtUtc, targetUtc),
                    ["channelId"] = sample.ChannelId,
                    ["channelName"] = channel?.Name ?? $"Channel {sample.ChannelId}",
                    ["channelSource"] = channel?.Source,
                    ["channelGroup"] = channel?.Group,
                    ["channelKind"] = channel?.Kind,
                    ["type"] = PayloadJson.ResolveTelemetryType(sample.ChannelId, channelMap),
                    ["value"] = sample.Value,
                    ["segmentId"] = sample.SegmentId
                };
            }));
    }

    private static JsonArray BuildNearestArtifactSnapshots(AppSessionSnapshot snapshot, DateTimeOffset targetUtc, TimeSpan window, int limit)
    {
        return PayloadJson.CreateJsonArray(snapshot.ArtifactSnapshots
            .Where(artifact => IsInsideWindow(artifact.CapturedAtUtc, targetUtc, window))
            .OrderBy(artifact => SessionReviewContext.CalculateDeltaMilliseconds(artifact.CapturedAtUtc, targetUtc))
            .ThenBy(artifact => artifact.SnapshotId, StringComparer.Ordinal)
            .Take(limit)
            .Select(artifact => (JsonNode?)BuildArtifactSnapshotPayload(artifact, targetUtc)));
    }

    private static JsonObject BuildArtifactSnapshotPayload(SessionArtifactSnapshot artifact, DateTimeOffset targetUtc)
    {
        return new JsonObject
        {
            ["snapshotId"] = artifact.SnapshotId,
            ["capturedAtUtc"] = artifact.CapturedAtUtc,
            ["deltaMilliseconds"] = SessionReviewContext.CalculateDeltaMilliseconds(artifact.CapturedAtUtc, targetUtc),
            ["source"] = artifact.Source,
            ["rootAlias"] = artifact.RootAlias,
            ["relativePath"] = artifact.RelativePath,
            ["name"] = artifact.Name,
            ["kind"] = artifact.Kind,
            ["artifactDirectoryName"] = artifact.ArtifactDirectoryName,
            ["directoryCount"] = artifact.DirectoryCount,
            ["fileCount"] = artifact.FileCount,
            ["byteCount"] = artifact.ByteCount,
            ["truncated"] = artifact.Truncated
        };
    }

    private static bool IsInsideWindow(DateTimeOffset timestampUtc, DateTimeOffset targetUtc, TimeSpan window)
        => Math.Abs((timestampUtc - targetUtc).TotalMilliseconds) <= window.TotalMilliseconds;

    private static TimeSpan CalculateAnnotationDistance(SessionAnnotation annotation, DateTimeOffset targetUtc)
    {
        var effectiveEndUtc = annotation.EndUtc ?? annotation.StartUtc;
        if (annotation.StartUtc <= targetUtc && effectiveEndUtc >= targetUtc)
        {
            return TimeSpan.Zero;
        }

        return annotation.StartUtc > targetUtc
            ? annotation.StartUtc - targetUtc
            : targetUtc - effectiveEndUtc;
    }
}
