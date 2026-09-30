using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal sealed class CallAppTool : RemoteAppOperation
{
    public CallAppTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_call_app_tool";

    protected override string Title => "Call App Tool";

    protected override string Description =>
        "Proxy a tool invocation into a live paired app, optionally capturing a visual tree or screenshot immediately afterward.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["toolId"] = ToolSchema.String("Remote app tool id to execute."),
            ["arguments"] = ToolSchema.Object(
                description: "Arguments forwarded directly to the remote tool.",
                properties: new Dictionary<string, ToolSchema>(),
                additionalProperties: true,
                nullable: true),
            ["after"] = RemoteAppToolSchemas.AfterEvidence(nullable: true)
        },
        required: ["toolId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => BuildCallAppToolResultAsync(arguments, correlationId);
}
