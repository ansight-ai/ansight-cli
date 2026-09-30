using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class FindDeadTouchesTool : Operation
{
    public FindDeadTouchesTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_find_dead_touches";

    protected override string Title => "Find Dead Touches";

    protected override string Description => "Find tap, long-press, and drag gestures with no response evidence or with nearby gesture timeout, blocked-recognizer, cancellation, rejection, or failure signals.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: DeadTouchProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "responseWindowMs",
                TouchReviewDefaults.DefaultDeadTouchResponseWindowMilliseconds,
                TouchReviewDefaults.MaxGestureGapMilliseconds,
                out var responseWindowMs,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeScreenshotsAsEvidence", defaultValue: false, out var includeScreenshotsAsEvidence, out errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(
                arguments,
                "limit",
                TouchReviewDefaults.DefaultTouchResultLimit,
                TouchReviewDefaults.MaxTouchResultLimit,
                out var limit,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid dead touch arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var gestures = TouchGestureSegmenter.BuildGestureSegments(touches, TimeSpan.FromMilliseconds(TouchReviewDefaults.DefaultGestureGapMilliseconds))
            .Where(gesture => gesture.Kind is "tap" or "longPress" or "drag")
            .ToArray();
        var deadGestures = gestures
            .Select(gesture => DeadTouchAnalyzer.BuildDeadTouchCandidate(snapshot!, gesture, TimeSpan.FromMilliseconds(responseWindowMs), includeScreenshotsAsEvidence))
            .Where(candidate => candidate.IsDead)
            .Take(limit)
            .Select(candidate => (JsonNode?)candidate.Payload)
            .ToArray();

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["evaluatedGestureCount"] = gestures.Length,
                ["returnedDeadTouchCount"] = deadGestures.Length,
                ["responseWindowMs"] = responseWindowMs,
                ["includeScreenshotsAsEvidence"] = includeScreenshotsAsEvidence,
                ["deadTouches"] = PayloadJson.CreateJsonArray(deadGestures)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> DeadTouchProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["responseWindowMs"] = ToolSchema.Integer("Milliseconds after each gesture to search for evidence. Defaults to 1200, max 10000.", nullable: true);
        properties["includeScreenshotsAsEvidence"] = ToolSchema.Boolean("Treat screenshots in the response window as evidence. Defaults to false.", nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of dead touch candidates to return. Defaults to 500, max 5000.", nullable: true);
        return properties;
    }
}
