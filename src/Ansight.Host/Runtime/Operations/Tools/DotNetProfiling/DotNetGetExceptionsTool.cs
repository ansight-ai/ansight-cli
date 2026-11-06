using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DotNetProfiling;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.DotNetProfiling;

internal sealed class DotNetGetExceptionsTool : DotNetAnalysisOperation
{
    public DotNetGetExceptionsTool(DotNetOperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_dotnet_get_exceptions";

    protected override string Title => "Get .NET Exceptions";

    protected override string Description => "Group thrown .NET exceptions by type and message within the selected trace range, including counts and first/last timestamps.";

    protected override JsonObject InputSchema
    {
        get
        {
            var properties = SelectionProperties();
            properties["limit"] = ToolSchema.Integer("Maximum exception groups to return. Defaults to 100, maximum 1000.", nullable: true);

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

        var groups = result!.Analysis.Exceptions.Take(limit).ToArray();
        var payload = new JsonObject
        {
            ["throwCount"] = result.Analysis.Exceptions.Sum(group => group.Count),
            ["groupCount"] = result.Analysis.Exceptions.Count,
            ["groups"] = JsonSerializer.SerializeToNode(groups, JsonUtil.Compact)
        };

        return Completed(ToolSuccess(DotNetOperationPayloads.AnalysisEnvelope(result, "exceptions", payload)));
    }
}
