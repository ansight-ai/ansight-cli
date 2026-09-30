using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class ExportTouchSliceTool : Operation
{
    public ExportTouchSliceTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_export_touch_slice";

    protected override string Title => "Export Touch Slice";

    protected override string Description => "Export a reproducible touch input slice with inferred gestures, optional nearby session artifacts, and Markdown notes.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ExportProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                TouchReviewDefaults.DefaultTouchResultLimit,
                TouchReviewDefaults.MaxTouchResultLimit,
                out var limit,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadExportFormat(arguments, out var format, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeLogs", defaultValue: true, out var includeLogs, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeScreenshots", defaultValue: true, out var includeScreenshots, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeAnnotations", defaultValue: true, out var includeAnnotations, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeVisualTrees", defaultValue: true, out var includeVisualTrees, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeArtifacts", defaultValue: true, out var includeArtifacts, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch export arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var returnedTouches = touches.Take(limit).ToArray();
        var range = TouchReviewFiltering.ResolveTouchRange(snapshot!, filters, returnedTouches.Length > 0 ? returnedTouches : touches);
        var gestures = TouchGestureSegmenter.BuildGestureSegments(returnedTouches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds)).ToArray();
        var payload = new JsonObject
        {
            ["format"] = format,
            ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
            ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
            ["matchedTouchCount"] = touches.Length,
            ["returnedTouchCount"] = returnedTouches.Length,
            ["isTruncated"] = touches.Length > returnedTouches.Length,
            ["startUtc"] = range.StartUtc,
            ["endUtc"] = range.EndUtc,
            ["touches"] = PayloadJson.CreateJsonArray(returnedTouches.Select((touch, index) => (JsonNode?)TouchReviewPayloads.BuildTouchPayload(touch, index))),
            ["gestures"] = PayloadJson.CreateJsonArray(gestures.Select(gesture => (JsonNode?)TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: false))),
            ["logs"] = includeLogs ? TouchArtifactPayloadBuilder.BuildLogsArray(snapshot!, range.StartUtc, range.EndUtc, limit) : new JsonArray(),
            ["screenshots"] = includeScreenshots ? TouchArtifactPayloadBuilder.BuildScreenshotsArray(snapshot!, range.StartUtc, range.EndUtc, limit) : new JsonArray(),
            ["annotations"] = includeAnnotations ? TouchArtifactPayloadBuilder.BuildAnnotationsArray(snapshot!, range.StartUtc, range.EndUtc, limit) : new JsonArray(),
            ["visualTrees"] = includeVisualTrees ? TouchArtifactPayloadBuilder.BuildVisualTreesArray(snapshot!, range.StartUtc, range.EndUtc, limit) : new JsonArray(),
            ["artifactSnapshots"] = includeArtifacts ? TouchArtifactPayloadBuilder.BuildArtifactSnapshotsArray(snapshot!, range.StartUtc, range.EndUtc, limit) : new JsonArray()
        };

        if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "both", StringComparison.OrdinalIgnoreCase))
        {
            payload["markdown"] = TouchSliceMarkdownBuilder.BuildMarkdownTouchSlice(snapshot!, returnedTouches, gestures, range.StartUtc, range.EndUtc);
        }

        return Task.FromResult(RequestResult.ToolResult(payload, isError: false));
    }

    private static Dictionary<string, ToolSchema> ExportProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["limit"] = ToolSchema.Integer("Maximum number of matching touches to include. Defaults to 500, max 5000.", nullable: true);
        properties["format"] = ToolSchema.String(
            "Export format. Defaults to both.",
            enumValues: ["json", "markdown", "both"],
            nullable: true);
        properties["includeLogs"] = ToolSchema.Boolean("Include logs in the exported time range. Defaults to true.", nullable: true);
        properties["includeScreenshots"] = ToolSchema.Boolean("Include screenshot frame summaries in the exported time range. Defaults to true.", nullable: true);
        properties["includeAnnotations"] = ToolSchema.Boolean("Include annotations overlapping the exported time range. Defaults to true.", nullable: true);
        properties["includeVisualTrees"] = ToolSchema.Boolean("Include visual tree snapshot summaries in the exported time range. Defaults to true.", nullable: true);
        properties["includeArtifacts"] = ToolSchema.Boolean("Include artifact snapshot summaries in the exported time range. Defaults to true.", nullable: true);
        return properties;
    }
}
