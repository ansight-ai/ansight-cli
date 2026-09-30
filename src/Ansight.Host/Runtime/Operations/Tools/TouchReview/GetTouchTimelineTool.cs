using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class GetTouchTimelineTool : Operation
{
    public GetTouchTimelineTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_touch_timeline";

    protected override string Title => "Get Touch Timeline";

    protected override string Description => "Bucket captured touch input over time with counts by action and unique pointer count.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TimelineProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "bucketCount",
                TouchReviewDefaults.DefaultTouchTimelineBucketCount,
                TouchReviewDefaults.MaxTouchTimelineBucketCount,
                out var bucketCount,
                out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch timeline arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var range = TouchReviewFiltering.ResolveTouchRange(snapshot!, filters, touches);
        var buckets = TouchTimelineBuilder.BuildTouchTimelineBuckets(touches, range.StartUtc, range.EndUtc, bucketCount);

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["matchedTouchCount"] = touches.Length,
                ["startUtc"] = range.StartUtc,
                ["endUtc"] = range.EndUtc,
                ["bucketCount"] = bucketCount,
                ["buckets"] = buckets
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> TimelineProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["bucketCount"] = ToolSchema.Integer("Number of timeline buckets. Defaults to 24, max 200.", nullable: true);
        return properties;
    }
}
