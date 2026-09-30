using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class GetNetworkRequestTool : Operation
{
    public GetNetworkRequestTool(OperationServices services) : base(services) { }

    public override string Name => "ansight_get_network_request";
    protected override string Title => "Get Network Request";
    protected override string Description => "Inspect one retained network request, preserving repeated headers. Body content is available separately through bounded body previews.";
    protected override JsonObject InputSchema => NetworkInspectionToolSchemas.Request();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            var requestId = NetworkInspectionArguments.ReadString(arguments, "requestId", required: true)!;
            if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var session, out var error))
            {
                return Task.FromResult(ToolError(error));
            }

            var request = session!.NetworkRequests.FirstOrDefault(item => string.Equals(item.Id, requestId, StringComparison.Ordinal));
            return Task.FromResult(request is null
                ? ToolError($"Network request '{requestId}' was not found in this session.")
                : NetworkInspectionPayload.Result(NetworkInspectionPayload.Detail(session, request)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(ToolError(exception.Message));
        }
    }
}
