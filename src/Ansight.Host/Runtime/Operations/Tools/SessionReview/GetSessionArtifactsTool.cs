using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionReview;

internal sealed class GetSessionArtifactsTool : Operation
{
    public GetSessionArtifactsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_session_artifacts";

    protected override string Title => "Get Session Artifacts";

    protected override string Description => "Return a manifest of a captured session's logs, screenshots, visual trees, annotations, telemetry, artifact snapshots, and analyses.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["platform"] = SessionInspectionToolSchemas.PlatformSchema("Optional platform key filter."),
            ["platforms"] = SessionInspectionToolSchemas.PlatformsSchema(),
            ["types"] = ToolSchema.Array(
                ToolSchema.String(
                    "Artifact category to include.",
                    enumValues:
                    [
                        "logs",
                        "screenshots",
                        "visualTrees",
                        "annotations",
                        "telemetry",
                        "artifactSnapshots",
                        "analyses",
                        "all"
                    ]),
                description: "Optional artifact categories to include. Defaults to all.",
                nullable: true),
            ["startUtc"] = ToolSchema.String("Optional inclusive start timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["endUtc"] = ToolSchema.String("Optional inclusive end timestamp in ISO-8601 UTC.", nullable: true, format: "date-time"),
            ["limit"] = ToolSchema.Integer("Maximum number of entries per included category. Defaults to 200, max 5000.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Unable to resolve session."));
        }

        if (!SessionReviewContext.TryReadTimeRange(arguments, out var startUtc, out var endUtc, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                OperationDefaults.DefaultArtifactManifestLimit,
                OperationDefaults.MaxArtifactManifestLimit,
                out var limit,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid session artifact filters."));
        }

        var requestedTypes = ReadRequestedTypes(arguments);
        var liveSessionIds = sessionResolver.GetLiveSessionIds();
        var payload = new JsonObject
        {
            ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, liveSessionIds.Contains(snapshot!.SessionId)),
            ["filters"] = new JsonObject
            {
                ["types"] = PayloadJson.CreateJsonArray(requestedTypes.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).Select(type => JsonValue.Create(type))),
                ["startUtc"] = startUtc,
                ["endUtc"] = endUtc,
                ["limit"] = limit
            },
            ["counts"] = new JsonObject
            {
                ["logs"] = snapshot.Logs.Count,
                ["screenshots"] = snapshot.Images.Count,
                ["visualTrees"] = snapshot.VisualTreeSnapshots.Count,
                ["annotations"] = snapshot.Annotations.Count,
                ["telemetryChannels"] = snapshot.MetricChannels.Count,
                ["telemetrySamples"] = snapshot.Metrics.Count,
                ["artifactSnapshots"] = snapshot.ArtifactSnapshots.Count,
                ["analyses"] = snapshot.Analyses.Count
            }
        };

        AddCategory(payload, requestedTypes, "logs", () => BuildLogs(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "screenshots", () => BuildScreenshots(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "visualTrees", () => BuildVisualTrees(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "annotations", () => BuildAnnotations(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "telemetry", () => BuildTelemetry(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "artifactSnapshots", () => BuildArtifactSnapshots(snapshot, startUtc, endUtc, limit));
        AddCategory(payload, requestedTypes, "analyses", () => BuildAnalyses(snapshot, startUtc, endUtc, limit));

        return Task.FromResult(RequestResult.ToolResult(payload, isError: false));
    }

    private static HashSet<string> ReadRequestedTypes(JsonObject? arguments)
    {
        var types = ArgumentReader.ReadStringSet(arguments, "types");
        if (types.Count == 0 || types.Contains("all"))
        {
            return new HashSet<string>(["logs", "screenshots", "visualTrees", "annotations", "telemetry", "artifactSnapshots", "analyses"], StringComparer.OrdinalIgnoreCase);
        }

        return types;
    }

    private static void AddCategory(JsonObject payload, IReadOnlySet<string> requestedTypes, string category, Func<JsonObject> buildPayload)
    {
        if (requestedTypes.Contains(category))
        {
            payload[category] = buildPayload();
        }
    }

    private static JsonObject BuildLogs(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
    {
        var matched = SessionReviewContext.BuildIndexedReviewLogs(snapshot)
            .Where(entry => PayloadJson.MatchesTimestamp(entry.Log.TimestampUtc, startUtc, endUtc))
            .OrderBy(entry => entry.Log.TimestampUtc)
            .ThenBy(entry => entry.Index)
            .ToArray();
        return new JsonObject
        {
            ["matchedCount"] = matched.Length,
            ["returnedCount"] = Math.Min(matched.Length, limit),
            ["isTruncated"] = matched.Length > limit,
            ["items"] = PayloadJson.CreateJsonArray(matched.Take(limit).Select(entry => (JsonNode?)new JsonObject
            {
                ["logIndex"] = entry.Index,
                ["timestampUtc"] = entry.Log.TimestampUtc,
                ["priority"] = entry.Log.Priority.ToString(),
                ["source"] = entry.Log.Source,
                ["tag"] = entry.Log.Tag,
                ["eventId"] = entry.Log.EventId,
                ["message"] = entry.Log.Message
            }))
        };
    }

    private static JsonObject BuildScreenshots(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
        => BuildCategory(snapshot.Images
            .Where(image => PayloadJson.MatchesTimestamp(image.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(image => image.CapturedAtUtc)
            .ThenBy(image => image.FrameId, StringComparer.Ordinal)
            .Select(image => (JsonNode?)new JsonObject
            {
                ["frameId"] = image.FrameId,
                ["capturedAtUtc"] = image.CapturedAtUtc,
                ["format"] = image.Format,
                ["width"] = image.Width,
                ["height"] = image.Height,
                ["quality"] = image.Quality,
                ["byteCount"] = image.ByteCount
            }), limit);

    private static JsonObject BuildVisualTrees(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
        => BuildCategory(snapshot.VisualTreeSnapshots
            .Where(tree => PayloadJson.MatchesTimestamp(tree.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(tree => tree.CapturedAtUtc)
            .ThenBy(tree => tree.SnapshotId, StringComparer.Ordinal)
            .Select(tree => (JsonNode?)PayloadJson.BuildSessionVisualTreeSnapshotSummaryPayload(snapshot, tree)), limit);

    private static JsonObject BuildAnnotations(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
        => BuildCategory(snapshot.Annotations
            .Where(annotation => SessionAnnotationInspection.MatchesAnnotationTimeRange(annotation, startUtc, endUtc))
            .OrderBy(annotation => annotation.StartUtc)
            .ThenBy(annotation => annotation.AnnotationId, StringComparer.Ordinal)
            .Select(annotation => (JsonNode?)PayloadJson.BuildSessionAnnotationPayload(annotation)), limit);

    private static JsonObject BuildTelemetry(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
    {
        var channelMap = snapshot.MetricChannels.ToDictionary(channel => channel.ChannelId);
        var channels = snapshot.Metrics
            .Where(sample => PayloadJson.MatchesTimestamp(sample.CapturedAtUtc, startUtc, endUtc))
            .GroupBy(sample => sample.ChannelId)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                channelMap.TryGetValue(group.Key, out var channel);
                var samples = group.OrderBy(sample => sample.CapturedAtUtc).ToArray();
                return (JsonNode?)new JsonObject
                {
                    ["channelId"] = group.Key,
                    ["name"] = channel?.Name ?? $"Channel {group.Key}",
                    ["type"] = PayloadJson.ResolveTelemetryType(group.Key, channelMap),
                    ["colorHex"] = channel?.ColorHex,
                    ["source"] = channel?.Source,
                    ["group"] = channel?.Group,
                    ["kind"] = channel?.Kind,
                    ["sampleCount"] = samples.Length,
                    ["firstSampleUtc"] = samples[0].CapturedAtUtc,
                    ["lastSampleUtc"] = samples[^1].CapturedAtUtc,
                    ["minimumValue"] = samples.Min(sample => sample.Value),
                    ["maximumValue"] = samples.Max(sample => sample.Value),
                    ["lastValue"] = samples[^1].Value
                };
            });
        return BuildCategory(channels, limit);
    }

    private static JsonObject BuildArtifactSnapshots(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
        => BuildCategory(snapshot.ArtifactSnapshots
            .Where(artifact => PayloadJson.MatchesTimestamp(artifact.CapturedAtUtc, startUtc, endUtc))
            .OrderBy(artifact => artifact.CapturedAtUtc)
            .ThenBy(artifact => artifact.SnapshotId, StringComparer.Ordinal)
            .Select(artifact => (JsonNode?)new JsonObject
            {
                ["snapshotId"] = artifact.SnapshotId,
                ["capturedAtUtc"] = artifact.CapturedAtUtc,
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
            }), limit);

    private static JsonObject BuildAnalyses(AppSessionSnapshot snapshot, DateTimeOffset? startUtc, DateTimeOffset? endUtc, int limit)
        => BuildCategory(snapshot.Analyses
            .Where(analysis => PayloadJson.MatchesTimestamp(analysis.StartedUtc, startUtc, endUtc)
                               || (analysis.CompletedUtc.HasValue && PayloadJson.MatchesTimestamp(analysis.CompletedUtc.Value, startUtc, endUtc)))
            .OrderBy(analysis => analysis.StartedUtc)
            .ThenBy(analysis => analysis.AnalysisId, StringComparer.Ordinal)
            .Select(analysis => (JsonNode?)new JsonObject
            {
                ["analysisId"] = analysis.AnalysisId,
                ["agentId"] = analysis.AgentId,
                ["analysisKind"] = analysis.AnalysisKind,
                ["startedUtc"] = analysis.StartedUtc,
                ["completedUtc"] = analysis.CompletedUtc,
                ["success"] = analysis.Success,
                ["statusMessage"] = analysis.StatusMessage,
                ["hasPrompt"] = !string.IsNullOrWhiteSpace(analysis.Prompt),
                ["hasTranscript"] = !string.IsNullOrWhiteSpace(analysis.Transcript),
                ["hasFinalResponse"] = !string.IsNullOrWhiteSpace(analysis.FinalResponse),
                ["hasMermaidDefinition"] = !string.IsNullOrWhiteSpace(analysis.MermaidDefinition)
            }), limit);

    private static JsonObject BuildCategory(IEnumerable<JsonNode?> items, int limit)
    {
        var itemArray = items.ToArray();
        return new JsonObject
        {
            ["matchedCount"] = itemArray.Length,
            ["returnedCount"] = Math.Min(itemArray.Length, limit),
            ["isTruncated"] = itemArray.Length > limit,
            ["items"] = PayloadJson.CreateJsonArray(itemArray.Take(limit))
        };
    }
}
