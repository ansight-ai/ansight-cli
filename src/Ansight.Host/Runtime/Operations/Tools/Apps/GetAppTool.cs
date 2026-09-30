using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Apps;

internal sealed class GetAppTool : AppOperation
{
    public GetAppTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_app";

    protected override string Title => "Get App";

    protected override string Description => "Return one app known to Ansight, including linked codebase, counts, and last-seen metadata.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Required app identifier or package id.")
        },
        required: ["appId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(BuildGetAppResult(arguments));
}
