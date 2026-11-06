using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetCpuHotspotsTool : DotNetAnalysisOperation
{
    public DotNetGetCpuHotspotsTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_cpu_hotspots";

    protected override string Title => "Get .NET CPU Hotspots";

    protected override string Description => "Rank symbolicated .NET methods by exclusive and inclusive sampled-thread counts within an optional trace range, process, or thread.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum methods to return. Defaults to 50, maximum 500.", nullable: true);

            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "limit", 50, 500, out var limit, out var limitError))
        {
            return Completed(ToolError(limitError!));
        }

        if (!TryAnalyze(arguments, out var result, out var error, hotspotLimit: limit))
        {
            return Completed(error!);
        }

        var payload = new JsonObject
        {
            ["sampleCount"] = result!.Analysis.Overview.CpuSampleCount,
            ["hotspotCount"] = result.Analysis.CpuHotspots.Count,
            ["hotspots"] = JsonSerializer.SerializeToNode(result.Analysis.CpuHotspots, JsonUtil.Compact)
        };

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "cpu", payload)));
    }
}
