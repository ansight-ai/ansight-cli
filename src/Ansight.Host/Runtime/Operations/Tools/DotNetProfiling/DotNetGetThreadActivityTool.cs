using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetThreadActivityTool : DotNetAnalysisOperation
{
    public DotNetGetThreadActivityTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_thread_activity";

    protected override string Title => "Get .NET Thread Activity";

    protected override string Description => "Rank processes and threads by captured sampled-thread activity for the selected NetTrace range.";

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

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(
            result!,
            "threads",
            JsonSerializer.SerializeToNode(result!.Analysis.Threads, JsonUtil.Compact))));
    }
}
