using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.TouchReview;

internal sealed class GetTouchHeatmapTool : Operation
{
    public GetTouchHeatmapTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_touch_heatmap";

    protected override string Title => "Get Touch Heatmap";

    protected override string Description => "Aggregate captured touch input into a normalized screen grid for spatial hotspot analysis.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: HeatmapProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !TouchReviewArgumentReader.TryReadTouchFilters(arguments, out var filters, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "columns",
                TouchReviewDefaults.DefaultHeatmapColumns,
                TouchReviewDefaults.MaxHeatmapDimension,
                out var columns,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBoundedPositiveInteger(
                arguments,
                "rows",
                TouchReviewDefaults.DefaultHeatmapRows,
                TouchReviewDefaults.MaxHeatmapDimension,
                out var rows,
                out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "includeEmpty", defaultValue: false, out var includeEmpty, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid touch heatmap arguments."));
        }

        var touches = TouchReviewFiltering.GetFilteredTouches(snapshot!, filters).ToArray();
        var cells = TouchHeatmapBuilder.BuildHeatmapCells(touches, columns, rows, includeEmpty);

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["filters"] = TouchReviewFiltering.BuildTouchFiltersPayload(filters),
                ["matchedTouchCount"] = touches.Length,
                ["columns"] = columns,
                ["rows"] = rows,
                ["cells"] = cells
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> HeatmapProperties()
    {
        var properties = SessionReviewToolSchemas.TouchFilterProperties();
        properties["columns"] = ToolSchema.Integer("Number of heatmap columns. Defaults to 6, max 100.", nullable: true);
        properties["rows"] = ToolSchema.Integer("Number of heatmap rows. Defaults to 10, max 100.", nullable: true);
        properties["includeEmpty"] = ToolSchema.Boolean("Include cells with zero touches. Defaults to false.", nullable: true);
        return properties;
    }
}
