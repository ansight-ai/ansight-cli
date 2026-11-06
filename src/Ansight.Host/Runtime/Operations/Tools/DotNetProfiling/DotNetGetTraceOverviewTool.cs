using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetTraceOverviewTool : DotNetAnalysisOperation
{
    public DotNetGetTraceOverviewTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_trace_overview";

    protected override string Title => "Get .NET Trace Overview";

    protected override string Description => "Summarize selected NetTrace duration, events, CPU samples, processes, threads, GC pauses, JIT work, exceptions, providers, and symbolication quality.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: SelectionProperties(),
        required: ["captureId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryAnalyze(arguments, out var result, out var error))
        {
            return Completed(error!);
        }

        var overview = JsonSerializer.SerializeToNode(result!.Analysis.Overview, JsonUtil.Compact)?.AsObject()
            ?? new JsonObject();
        overview["providerEventCounts"] = JsonSerializer.SerializeToNode(
            result.Analysis.ProviderEventCounts,
            JsonUtil.Compact);
        overview["eventNameCounts"] = JsonSerializer.SerializeToNode(
            result.Analysis.EventNameCounts,
            JsonUtil.Compact);

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "overview", overview)));
    }
}
