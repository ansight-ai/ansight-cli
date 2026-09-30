using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RemoteApp;

internal sealed class PushFileToAppTool : RemoteAppOperation
{
    public PushFileToAppTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_push_file_to_app";

    protected override string Title => "Push File To App";

    protected override string Description => "Read a host-local file and push it into a folder inside a live paired app sandbox through the app's files.push_file tool.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true),
            ["localFilePath"] = ToolSchema.String("Host-local file path to read and push into the app."),
            ["root"] = ToolSchema.String("Optional destination sandbox root alias.", nullable: true),
            ["directoryPath"] = ToolSchema.String("Destination folder path relative to the sandbox root."),
            ["fileName"] = ToolSchema.String("Optional destination file name. Defaults to the local file name.", nullable: true),
            ["overwrite"] = ToolSchema.Boolean("Replace an existing app sandbox file with the same name."),
            ["createDirectory"] = ToolSchema.Boolean("Create the destination folder in the app sandbox when missing.")
        },
        required: ["localFilePath", "directoryPath"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => BuildPushFileToAppResultAsync(arguments, correlationId);
}
