using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetGcSummaryTool : DotNetAnalysisOperation
{
    public DotNetGetGcSummaryTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_gc_summary";

    protected override string Title => "Get .NET GC Summary";

    protected override string Description => "Summarize captured .NET garbage collections and return the longest GC intervals in the selected trace range.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum GC intervals to return. Defaults to 100, maximum 1000.", nullable: true);

            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 100, 1000, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var pauses = result!.Analysis.GcPauses
            .OrderByDescending(pause => pause.DurationMilliseconds)
            .Take(limit)
            .ToArray();
        var payload = new JsonObject
        {
            ["collectionCount"] = result.Analysis.GcPauses.Count,
            ["totalIntervalMilliseconds"] = result.Analysis.GcPauses.Sum(pause => pause.DurationMilliseconds),
            ["longestIntervalMilliseconds"] = result.Analysis.GcPauses.Count == 0
                ? 0
                : result.Analysis.GcPauses.Max(pause => pause.DurationMilliseconds),
            ["generationCounts"] = JsonSerializer.SerializeToNode(
                result.Analysis.GcPauses
                    .GroupBy(pause => pause.Generation)
                    .OrderBy(group => group.Key)
                    .ToDictionary(group => $"generation-{group.Key}", group => group.Count()),
                JsonUtil.Compact),
            ["intervals"] = JsonSerializer.SerializeToNode(pauses, JsonUtil.Compact)
        };

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "gc", payload)));
    }
}
