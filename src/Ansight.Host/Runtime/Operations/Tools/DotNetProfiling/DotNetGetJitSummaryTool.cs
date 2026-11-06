using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetJitSummaryTool : DotNetAnalysisOperation
{
    public DotNetGetJitSummaryTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_jit_summary";

    protected override string Title => "Get .NET JIT Summary";

    protected override string Description => "Summarize captured .NET JIT activity and rank methods by IL size within the selected trace range.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum JIT methods to return. Defaults to 100, maximum 1000.", nullable: true);

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

        var methods = result!.Analysis.JittedMethods
            .OrderByDescending(method => method.IlSize)
            .ThenBy(method => method.TimestampMilliseconds)
            .Take(limit)
            .ToArray();
        var payload = new JsonObject
        {
            ["methodCount"] = result.Analysis.JittedMethods.Count,
            ["totalIlBytes"] = result.Analysis.JittedMethods.Sum(method => (long)method.IlSize),
            ["methods"] = JsonSerializer.SerializeToNode(methods, JsonUtil.Compact)
        };

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "jit", payload)));
    }
}
