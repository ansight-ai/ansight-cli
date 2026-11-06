using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetCallTreeTool : DotNetAnalysisOperation
{
    public DotNetGetCallTreeTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_call_tree";

    protected override string Title => "Get .NET Call Tree";

    protected override string Description => "Return a bounded root-to-leaf .NET sampled call tree for the selected trace range, process, or thread.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["maxDepth"] = ToolSchema.Integer("Maximum returned stack depth. Defaults to 32, maximum 128.", nullable: true);
            properties["maxChildren"] = ToolSchema.Integer("Maximum children per call-tree node. Defaults to 50, maximum 200.", nullable: true);

            return ToolSchema.Object(properties: properties, required: ["captureId"], additionalProperties: false).ToJson();
        }
    }

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!ArgumentReader.TryReadPositiveLimit(arguments, "maxDepth", 32, 128, out var maxDepth, out var depthError))
        {
            return Completed(ToolError(depthError!));
        }

        if (!ArgumentReader.TryReadPositiveLimit(arguments, "maxChildren", 50, 200, out var maxChildren, out var childError))
        {
            return Completed(ToolError(childError!));
        }

        if (!TryAnalyze(
            arguments,
            out var result,
            out var error,
            callTreeDepth: maxDepth,
            callTreeChildLimit: maxChildren))
        {
            return Completed(error!);
        }

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(
            result!,
            "callTree",
            JsonSerializer.SerializeToNode(result!.Analysis.CallTree, JsonUtil.Compact))));
    }
}
