using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.Apps;

internal sealed class RegisterAppTool : AppOperation
{
    public RegisterAppTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_register_app";

    protected override string Title => "Register App";

    protected override string Description => "Register a new app entry in Ansight so it can be tracked, linked to a codebase, and used for enrollment invite issuance.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["appId"] = ToolSchema.String("Required app identifier or package id."),
            ["appName"] = ToolSchema.String("Optional display name. Defaults to appId when omitted.", nullable: true),
            ["codebasePath"] = ToolSchema.String("Optional existing local folder to associate with the app.", nullable: true)
        },
        required: ["appId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => Task.FromResult(BuildRegisterAppResult(arguments));
}
