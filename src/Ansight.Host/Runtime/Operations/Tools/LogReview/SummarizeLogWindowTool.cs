using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed class SummarizeLogWindowTool : Operation
{
    private readonly LogReviewActions logReview;

    public SummarizeLogWindowTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_summarize_log_window";

    protected override string Title => "Summarize Log Window";

    protected override string Description => "Summarize captured logs in a filtered time window, including priority, tag, source, and repeated-message counts.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: LogReviewProperties("Maximum number of top facet values and repeated messages to return. Defaults to 10, max 50."),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(logReview.BuildSummarizeLogWindowResult(arguments));

    private static Dictionary<string, ToolSchema> LogReviewProperties(string topCountDescription)
    {
        var properties = SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: false);
        properties["topCount"] = ToolSchema.Integer(topCountDescription, nullable: true);
        return properties;
    }
}
