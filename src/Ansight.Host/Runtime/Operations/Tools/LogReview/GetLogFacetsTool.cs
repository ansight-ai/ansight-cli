using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed class GetLogFacetsTool : Operation
{
    private readonly LogReviewActions logReview;

    public GetLogFacetsTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_get_log_facets";

    protected override string Title => "Get Log Facets";

    protected override string Description => "Return facet counts for captured logs across sessions, including app, platform, priority, tag, source, and session breakdowns.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: LogReviewProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(logReview.BuildGetLogFacetsResult(arguments));

    private static Dictionary<string, ToolSchema> LogReviewProperties()
    {
        var properties = SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: false);
        properties["topCount"] = ToolSchema.Integer("Maximum number of tag/source/session facet values to return. Defaults to 10, max 50.", nullable: true);
        return properties;
    }
}
