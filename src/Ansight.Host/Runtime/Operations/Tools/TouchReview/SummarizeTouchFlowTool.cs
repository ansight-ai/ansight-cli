using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class SummarizeTouchFlowTool : Operation
{
    public SummarizeTouchFlowTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_summarize_touch_flow";

    protected override string Title => "Summarize Touch Flow";

    protected override string Description => "Summarize captured touch input, inferred gesture counts, pointer counts, and coordinate metadata.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SummaryProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "gestureGapMs",
                TouchReviewDefaults.DefaultGestureGapMilliseconds,
                TouchReviewDefaults.MaxGestureGapMilliseconds,
                out var gestureGapMs,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch summary arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, TimeSpan.FromMilliseconds(gestureGapMs)).ToArray();

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["touchCount"] = touches.Length,
                ["gestureCount"] = gestures.Length,
                ["firstTouchUtc"] = touches.Length == 0 ? null : touches[0].CapturedAtUtc,
                ["lastTouchUtc"] = touches.Length == 0 ? null : touches[^1].CapturedAtUtc,
                ["durationMs"] = touches.Length <= 1 ? 0 : (long)Math.Round((touches[^1].CapturedAtUtc - touches[0].CapturedAtUtc).TotalMilliseconds),
                ["actionCounts"] = TouchReviewPayloads.BuildStringCountsArray(touches.Select(touch => TouchReviewGeometry.NormalizeTouchAction(touch.Action))),
                ["pointerCounts"] = TouchReviewPayloads.BuildIntegerCountsArray(touches.Select(touch => touch.PointerCount)),
                ["coordinateSpaces"] = TouchReviewPayloads.BuildStringCountsArray(touches.Select(touch => touch.CoordinateSpace)),
                ["coordinateUnits"] = TouchReviewPayloads.BuildStringCountsArray(touches.Select(touch => touch.CoordinateUnit)),
                ["gestureKindCounts"] = TouchReviewPayloads.BuildStringCountsArray(gestures.Select(gesture => gesture.Kind)),
                ["incompleteGestureCount"] = gestures.Count(gesture => !gesture.IsComplete),
                ["averageGestureDurationMs"] = gestures.Length == 0 ? 0 : gestures.Average(gesture => gesture.DurationMilliseconds),
                ["averageGestureDistance"] = gestures.Length == 0 ? 0 : gestures.Average(gesture => gesture.DistanceNormalized.GetValueOrDefault()),
                ["notableGestures"] = PayloadJson.CreateJsonArray(gestures
                    .OrderByDescending(gesture => gesture.DurationMilliseconds)
                    .ThenBy(gesture => gesture.StartUtc)
                    .Take(10)
                    .Select(gesture => (JsonNode?)TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: false)))
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> SummaryProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["gestureGapMs"] = ToolSchema.Integer("Maximum gap between touches for the same pointer before starting a new gesture. Defaults to 700, max 10000.", nullable: true);
        return properties;
    }
}
