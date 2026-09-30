using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.FocusedControls;

internal sealed class ListFocusedControlsTool : FocusedControlOperation
{
    public ListFocusedControlsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_focused_controls";

    protected override string Title => "List Focused Controls";

    protected override string Description => "List control selections supplied by connected host clients, including node metadata and bounds. The resident CLI host does not create selections; use ansight_find_ui for live target discovery.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Optional session id filter.", nullable: true),
            ["appId"] = ToolSchema.String("Optional app id filter.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(RequestResult.ToolResult(BuildListFocusedControlsPayload(arguments), isError: false));
}
