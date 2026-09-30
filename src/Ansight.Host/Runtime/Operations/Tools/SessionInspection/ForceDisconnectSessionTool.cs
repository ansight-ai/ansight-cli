using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class ForceDisconnectSessionTool : Operation
{
    public ForceDisconnectSessionTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_force_disconnect_session";

    protected override string Title => "Force Disconnect Session";

    protected override string Description => "Force-disconnect a currently connected live app session.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session id to disconnect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists for that app.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveLiveSession(arguments, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        var result = appToolBridge.ForceDisconnectSession(snapshot!.SessionId);
        if (!result.IsSuccess)
        {
            return Task.FromResult(RequestResult.ToolResult(
                new JsonObject
                {
                    ["message"] = result.Message,
                    ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, isLive: true)
                },
                isError: true));
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["message"] = result.Message,
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, isLive: true)
            },
            isError: false));
    }
}
