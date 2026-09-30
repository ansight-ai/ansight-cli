using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetSessionPropertiesTool : Operation
{
    public GetSessionPropertiesTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_session_properties";

    protected override string Title => "Get Session Properties";

    protected override string Description =>
        "Return the custom properties reported for a live or historical Ansight session.";

    protected override JsonObject InputSchema => SessionInspectionToolSchemas.SessionSelectorSchema();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var snapshot, out var resolutionError))
        {
            return Task.FromResult(ToolError(resolutionError));
        }

        var isLive = sessionResolver.GetLiveSessionIds().Contains(snapshot!.SessionId);
        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["sessionId"] = snapshot.SessionId,
                ["appId"] = snapshot.AppId,
                ["captureSource"] = snapshot.CaptureSource,
                ["isLive"] = isLive,
                ["isHistorical"] = snapshot.IsHistorical,
                ["customProperties"] = snapshot.CustomProperties?.DeepClone() ?? new JsonObject()
            },
            isError: false));
    }
}
