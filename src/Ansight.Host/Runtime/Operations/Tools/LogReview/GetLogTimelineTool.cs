using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed class GetLogTimelineTool : Operation
{
    private readonly LogReviewActions logReview;

    public GetLogTimelineTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_get_log_timeline";

    protected override string Title => "Get Log Timeline";

    protected override string Description => "Bucket captured logs over time for trend review, with counts by priority in each bucket.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: TimelineProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(logReview.BuildGetLogTimelineResult(arguments));

    private static Dictionary<string, ToolSchema> TimelineProperties()
    {
        var properties = SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: false);
        properties["bucketCount"] = ToolSchema.Integer("Number of timeline buckets. Defaults to 24, max 200.", nullable: true);
        return properties;
    }
}
