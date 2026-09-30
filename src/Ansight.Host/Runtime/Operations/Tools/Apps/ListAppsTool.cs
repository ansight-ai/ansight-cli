using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Apps;

internal sealed class ListAppsTool : AppOperation
{
    public ListAppsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_apps";

    protected override string Title => "List Linked Apps";

    protected override string Description => "List apps known to Ansight, including linked codebases and live session counts.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        description: "No arguments are required.",
        properties: new Dictionary<string, ToolSchema>(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(RequestResult.ToolResult(BuildListAppsPayload(), isError: false));
}
