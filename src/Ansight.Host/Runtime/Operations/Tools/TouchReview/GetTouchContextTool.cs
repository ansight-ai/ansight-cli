using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class GetTouchContextTool : Operation
{
    public GetTouchContextTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_touch_context";

    protected override string Title => "Get Touch Context";

    protected override string Description => "Return surrounding touch input and nearby session artifacts for a selected touch or timestamp.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ContextProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedNonNegativeInteger(
                arguments,
                "before",
                TouchReviewDefaults.DefaultTouchContextRadius,
                TouchReviewDefaults.MaxTouchContextRadius,
                out var before,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedNonNegativeInteger(
                arguments,
                "after",
                TouchReviewDefaults.DefaultTouchContextRadius,
                TouchReviewDefaults.MaxTouchContextRadius,
                out var after,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "artifactWindowSeconds",
                TouchReviewDefaults.DefaultTouchArtifactWindowSeconds,
                TouchReviewDefaults.MaxTouchArtifactWindowSeconds,
                out var artifactWindowSeconds,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch context arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        if (touches.Length == 0)
        {
            return Task.FromResult(ToolError($"Session '{snapshot!.SessionId}' has no matching touch input."));
        }

        if (!TouchTargetResolver.TryResolveTouchTarget(arguments, touches, out var targetIndex, out var targetSelector, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Touch target was not found."));
        }

        var startIndex = Math.Max(0, targetIndex - before);
        var endIndex = Math.Min(touches.Length - 1, targetIndex + after);
        var contextTouches = touches
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .Select((touch, offset) => (JsonNode?)TouchReviewPayloads.BuildTouchPayload(touch, startIndex + offset, isTarget: startIndex + offset == targetIndex))
            .ToArray();
        var targetTouch = touches[targetIndex];

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["targetSelector"] = targetSelector,
                ["targetTouchIndex"] = targetIndex,
                ["targetTouch"] = TouchReviewPayloads.BuildTouchPayload(targetTouch, targetIndex, isTarget: true),
                ["before"] = before,
                ["after"] = after,
                ["totalMatchingTouchCount"] = touches.Length,
                ["returnedTouchCount"] = contextTouches.Length,
                ["hasEarlierTouches"] = startIndex > 0,
                ["hasLaterTouches"] = endIndex < touches.Length - 1,
                ["touches"] = PayloadJson.CreateJsonArray(contextTouches),
                ["artifacts"] = TouchArtifactPayloadBuilder.BuildTouchArtifactsPayload(
                    snapshot!,
                    targetTouch.CapturedAtUtc,
                    targetTouch.CapturedAtUtc,
                    TimeSpan.FromSeconds(artifactWindowSeconds),
                    TouchReviewDefaults.DefaultTouchArtifactLimitPerType)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ContextProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["touchId"] = ToolSchema.String("Optional touch id to use as the context target.", nullable: true);
        properties["touchIndex"] = ToolSchema.Integer("Optional zero-based index in the filtered, timestamp-ordered touch stream.", nullable: true);
        properties["timestampUtc"] = ToolSchema.String("Optional timestamp in ISO-8601 UTC; the nearest matching touch is used.", nullable: true, format: "date-time");
        properties["before"] = ToolSchema.Integer("Number of touches before the target to return. Defaults to 12, max 100.", nullable: true);
        properties["after"] = ToolSchema.Integer("Number of touches after the target to return. Defaults to 12, max 100.", nullable: true);
        properties["artifactWindowSeconds"] = ToolSchema.Integer("Seconds around the target touch used to collect nearby artifacts. Defaults to 10, max 3600.", nullable: true);
        return properties;
    }
}
