using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetAppStateTool : Operation
{
    public GetAppStateTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_app_state";

    protected override string Title => "Get App State";

    protected override string Description => "Return the current or last-known app lifecycle state for a live or historical Ansight session.";

    protected override JsonObject InputSchema => SessionInspectionToolSchemas.SessionSelectorSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        return Task.FromResult(RequestResult.ToolResult(
            PayloadJson.BuildSessionPayload(snapshot!, sessionResolver.GetLiveSessionIds().Contains(snapshot!.SessionId)),
            isError: false));
    }
}
