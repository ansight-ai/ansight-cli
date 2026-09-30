using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class GetTouchArtifactsTool : Operation
{
    public GetTouchArtifactsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_touch_artifacts";

    protected override string Title => "Get Touch Artifacts";

    protected override string Description => "Return logs, screenshots, annotations, visual trees, and artifact snapshots near a selected touch, gesture, or timestamp.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ArtifactProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "windowSeconds",
                TouchReviewDefaults.DefaultTouchArtifactWindowSeconds,
                TouchReviewDefaults.MaxTouchArtifactWindowSeconds,
                out var windowSeconds,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "limitPerType",
                TouchReviewDefaults.DefaultTouchArtifactLimitPerType,
                TouchReviewDefaults.MaxTouchArtifactLimitPerType,
                out var limitPerType,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch artifact arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        if (touches.Length == 0)
        {
            return Task.FromResult(ToolError($"Session '{snapshot!.SessionId}' has no matching touch input."));
        }

        if (!TouchTargetResolver.TryResolveTouchArtifactTarget(arguments, touches, out var targetStartUtc, out var targetEndUtc, out var targetPayload, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Touch artifact target was not found."));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["target"] = targetPayload,
                ["windowSeconds"] = windowSeconds,
                ["limitPerType"] = limitPerType,
                ["artifacts"] = TouchArtifactPayloadBuilder.BuildTouchArtifactsPayload(snapshot!, targetStartUtc, targetEndUtc, TimeSpan.FromSeconds(windowSeconds), limitPerType)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ArtifactProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["gestureId"] = ToolSchema.String("Optional gesture id returned by ansight_get_gesture_segments.", nullable: true);
        properties["touchId"] = ToolSchema.String("Optional touch id to use as the artifact target.", nullable: true);
        properties["touchIndex"] = ToolSchema.Integer("Optional zero-based index in the filtered, timestamp-ordered touch stream.", nullable: true);
        properties["timestampUtc"] = ToolSchema.String("Optional timestamp in ISO-8601 UTC to use as the artifact target.", nullable: true, format: "date-time");
        properties["windowSeconds"] = ToolSchema.Integer("Seconds around the target used to collect nearby artifacts. Defaults to 10, max 3600.", nullable: true);
        properties["limitPerType"] = ToolSchema.Integer("Maximum nearest entries per artifact type. Defaults to 5, max 50.", nullable: true);
        return properties;
    }
}
