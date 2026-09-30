using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.SessionInspection;

internal sealed class ReadNetworkBodyTool : Operation
{
    public ReadNetworkBodyTool(OperationServices services) : base(services) { }

    public override string Name => "ansight_read_network_body";
    protected override string Title => "Read Network Body";
    protected override string Description => "Read a bounded UTF-8 or Base64 prefix of a retained request or response body. Missing body data returns null; capture and preview truncation are reported separately.";
    protected override JsonObject InputSchema => NetworkInspectionToolSchemas.Request(body: true);

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        try
        {
            var requestId = NetworkInspectionArguments.ReadString(arguments, "requestId", required: true)!;
            var side = NetworkInspectionArguments.ReadString(arguments, "side", required: true)!;
            if (side is not ("request" or "response"))
            {
                return Task.FromResult(ToolError("side must be request or response."));
            }

            var maximumBytes = NetworkInspectionArguments.ReadLimit(arguments, "maxBytes", 16 * 1024, 64 * 1024);
            if (!sessionResolver.TryResolveSession(arguments, requireLiveSession: false, out var session, out var error))
            {
                return Task.FromResult(ToolError(error));
            }

            var request = session!.NetworkRequests.FirstOrDefault(item => string.Equals(item.Id, requestId, StringComparison.Ordinal));
            return Task.FromResult(request is null
                ? ToolError($"Network request '{requestId}' was not found in this session.")
                : NetworkInspectionPayload.Result(NetworkInspectionPayload.ReadBody(session, request, side, maximumBytes)));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(ToolError(exception.Message));
        }
    }
}
