using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal sealed class CallAppToolsTool : RemoteAppOperation
{
    public CallAppToolsTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_call_app_tools";

    protected override string Title => "Call App Tools";

    protected override string Description =>
        "Execute up to 32 live app tool calls in order, with optional per-call post-action evidence.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["calls"] = ToolSchema.Array(
                ToolSchema.Object(
                    properties: new Dictionary<string, ToolSchema>
                    {
                        ["callId"] = ToolSchema.String("Optional caller-defined result correlation id.", nullable: true),
                        ["toolId"] = ToolSchema.String("Remote app tool id to execute."),
                        ["arguments"] = ToolSchema.Object(
                            description: "Arguments forwarded to this remote tool.",
                            properties: new Dictionary<string, ToolSchema>(),
                            additionalProperties: true,
                            nullable: true),
                        ["after"] = RemoteAppToolSchemas.AfterEvidence(nullable: true)
                    },
                    required: ["toolId"],
                    additionalProperties: false),
                description: "Ordered calls. The app accepts between 1 and 32.",
                nullable: false),
            ["continueOnError"] = ToolSchema.Boolean(
                "Continue with later calls after an error. Defaults to false.",
                nullable: true)
        },
        required: ["calls"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => BuildCallAppToolsResultAsync(arguments, correlationId);
}
