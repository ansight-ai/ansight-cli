using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.FocusedControls;

internal sealed class GetFocusedControlTool : FocusedControlOperation
{
    public GetFocusedControlTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_focused_control";

    protected override string Title => "Get Focused Control";

    protected override string Description => "Return a control selection supplied by a connected host client, including the node snapshot, local bounds, absolute bounds, and normalized screenshot bounds. When no selection exists, use ansight_find_ui with an explicit selector.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one focused control exists for that app.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(BuildGetFocusedControlResult(arguments));
}
