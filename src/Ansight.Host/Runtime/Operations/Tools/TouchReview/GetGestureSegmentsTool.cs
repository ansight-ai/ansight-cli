using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class GetGestureSegmentsTool : Operation
{
    public GetGestureSegmentsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_gesture_segments";

    protected override string Title => "Get Gesture Segments";

    protected override string Description => "Return inferred gesture segments from captured touch input, including tap, long press, drag, multi-touch, and incomplete gestures.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: GestureProperties(),
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
                out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                TouchReviewDefaults.DefaultTouchResultLimit,
                TouchReviewDefaults.MaxTouchResultLimit,
                out var limit,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid gesture segment arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, TimeSpan.FromMilliseconds(gestureGapMs)).ToArray();
        var returnedGestures = gestures.Take(limit).ToArray();

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["matchedGestureCount"] = gestures.Length,
                ["returnedGestureCount"] = returnedGestures.Length,
                ["isTruncated"] = gestures.Length > returnedGestures.Length,
                ["gestures"] = PayloadJson.CreateJsonArray(returnedGestures.Select(gesture => (JsonNode?)TouchReviewPayloads.BuildGesturePayload(gesture, includeTouches: true)))
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> GestureProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["gestureGapMs"] = ToolSchema.Integer("Maximum gap between touches for the same pointer before starting a new gesture. Defaults to 700, max 10000.", nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of gestures to return. Defaults to 500, max 5000.", nullable: true);
        return properties;
    }
}
