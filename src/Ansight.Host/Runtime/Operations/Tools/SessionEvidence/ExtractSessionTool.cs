using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ExtractSessionTool : Operation
{
    public ExtractSessionTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_extract_session";

    protected override string Title => "Extract Session";

    protected override string Description => "Create a new captured session from a timeline range or an existing annotation's timeline bounds without changing the source session.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ExtractProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Select a session to extract."));
        }

        var source = NormalizeOptionalString(arguments?["source"]?.GetValue<string>());
        var annotationId = NormalizeOptionalString(arguments?["annotationId"]?.GetValue<string>());
        var name = NormalizeOptionalString(arguments?["name"]?.GetValue<string>());
        source ??= annotationId is null ? "timeRange" : "annotation";

        var sourcePayload = new JsonObject();
        SessionExtractionResult result;
        if (string.Equals(source, "annotation", StringComparison.OrdinalIgnoreCase))
        {
            if (annotationId is null)
            {
                return Task.FromResult(ToolError("annotationId is required when source is annotation."));
            }

            var annotation = snapshot!.Annotations.FirstOrDefault(candidate =>
                string.Equals(candidate.AnnotationId, annotationId, StringComparison.Ordinal));
            if (annotation is null)
            {
                return Task.FromResult(ToolError($"Annotation '{annotationId}' was not found for session '{snapshot.SessionId}'."));
            }

            sourcePayload = new JsonObject
            {
                ["kind"] = "annotation",
                ["annotationId"] = annotationId,
                ["annotation"] = PayloadJson.BuildSessionAnnotationPayload(annotation)
            };
            result = runtimeState.ExtractSessionAnnotationBounds(snapshot.SessionId, annotationId, name);
        }
        else if (string.Equals(source, "timeRange", StringComparison.OrdinalIgnoreCase))
        {
            if (!ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "startUtc", out var startUtc, out errorMessage)
                || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "endUtc", out var endUtc, out errorMessage))
            {
                return Task.FromResult(ToolError(errorMessage ?? "Invalid extraction range."));
            }

            if (!startUtc.HasValue || !endUtc.HasValue)
            {
                return Task.FromResult(ToolError("startUtc and endUtc are required when source is timeRange."));
            }

            sourcePayload = new JsonObject
            {
                ["kind"] = "timeRange",
                ["startUtc"] = startUtc.Value.ToUniversalTime(),
                ["endUtc"] = endUtc.Value.ToUniversalTime()
            };
            result = runtimeState.ExtractSessionTimelineRange(snapshot!.SessionId, startUtc.Value, endUtc.Value, name);
        }
        else
        {
            return Task.FromResult(ToolError("source must be one of: timeRange, annotation."));
        }

        if (!result.IsSuccess || result.ExtractedSession is null)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = result.Message,
                    ["sourceSession"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                    ["source"] = sourcePayload
                },
                isError: true));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sourceSession"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["extractedSession"] = SessionReviewContext.BuildSessionHeaderPayload(result.ExtractedSession, isLive: false),
                ["source"] = sourcePayload,
                ["counts"] = BuildExtractedCountsPayload(result.ExtractedSession)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ExtractProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["source"] = ToolSchema.String(
            "Extraction source. Use timeRange with startUtc/endUtc, or annotation with annotationId. Defaults to annotation when annotationId is supplied, otherwise timeRange.",
            enumValues: ["timeRange", "annotation"],
            nullable: true);
        properties["startUtc"] = ToolSchema.String("Inclusive extraction start timestamp in ISO-8601 UTC when source is timeRange.", nullable: true, format: "date-time");
        properties["endUtc"] = ToolSchema.String("Inclusive extraction end timestamp in ISO-8601 UTC when source is timeRange.", nullable: true, format: "date-time");
        properties["annotationId"] = ToolSchema.String("Annotation id whose timeline bounds should be extracted when source is annotation.", nullable: true);
        properties["name"] = ToolSchema.String("Optional display name for the extracted session.", nullable: true);
        return properties;
    }

    private static JsonObject BuildExtractedCountsPayload(AppSessionSnapshot snapshot)
    {
        return new JsonObject
        {
            ["logs"] = snapshot.Logs.Count,
            ["screenshots"] = snapshot.Images.Count,
            ["touches"] = snapshot.Touches.Count,
            ["visualTrees"] = snapshot.VisualTreeSnapshots.Count,
            ["artifactSnapshots"] = snapshot.ArtifactSnapshots.Count,
            ["annotations"] = snapshot.Annotations.Count,
            ["metricChannels"] = snapshot.MetricChannels.Count,
            ["metricSamples"] = snapshot.Metrics.Count
        };
    }
}
