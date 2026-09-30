using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.LogReview;

internal sealed class ExportLogSliceTool : Operation
{
    private readonly LogReviewActions logReview;

    public ExportLogSliceTool(OperationServices services)
        : base(services)
    {
        logReview = new LogReviewActions(services);
    }

    public override string Name => "ansight_export_log_slice";

    protected override string Title => "Export Log Slice";

    protected override string Description => "Export a reproducible log slice with optional overlapping annotations, screenshots, telemetry summary, and Markdown notes.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ExportProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(logReview.BuildExportLogSliceResult(arguments));

    private static Dictionary<string, ToolSchema> ExportProperties()
    {
        var properties = SessionInspectionToolSchemas.LogReviewFilterProperties(includeRequiredQuery: false);
        properties["limit"] = ToolSchema.Integer("Maximum number of matching logs to include. Defaults to 200, max 5000.", nullable: true);
        properties["format"] = ToolSchema.String(
            "Export format. Defaults to both.",
            enumValues: ["json", "markdown", "both"],
            nullable: true);
        properties["includeAnnotations"] = ToolSchema.Boolean("Include annotations overlapping the exported time range. Defaults to true.", nullable: true);
        properties["includeScreenshots"] = ToolSchema.Boolean("Include screenshot frame summaries in the exported time range. Defaults to true.", nullable: true);
        properties["includeTelemetrySummary"] = ToolSchema.Boolean("Include telemetry sample summaries in the exported time range. Defaults to true.", nullable: true);
        return properties;
    }
}
