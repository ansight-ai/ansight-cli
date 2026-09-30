namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

internal interface IAppToolBridge
{
    event EventHandler? ConnectionsChanged;

    IReadOnlyList<string> GetConnectedSessionIds();

    bool IsSessionConnected(string sessionId);

    OperationResult ForceDisconnectSession(string sessionId);

    Task<AppToolBridgeResponse> QueryToolsAsync(
        string sessionId,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null);

    Task<AppToolBridgeResponse> CallToolAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null);

    Task<AppToolBridgeResponse> QueryToolsSinceAsync(
        string sessionId,
        string? ifRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => QueryToolsAsync(sessionId, cancellationToken, requestContext);

    async Task<AppToolBridgeResponse> QueryToolsFilteredAsync(
        string sessionId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
    {
        var response = await QueryToolsSinceAsync(
            sessionId,
            arguments?["ifRevision"]?.GetValue<string>(),
            cancellationToken,
            requestContext).ConfigureAwait(false);
        if (!response.Success
            || response.Envelope?.Payload is not JsonObject payload)
        {
            return response;
        }

        var filteredPayload = Ansight.Host.Runtime.Operations.Tools.RemoteApp.RemoteAppToolCatalogFilter.Apply(
            payload,
            arguments);
        return response with
        {
            Envelope = new ToolProtocolEnvelope
            {
                Type = response.Envelope.Type,
                Id = response.Envelope.Id,
                ReplyTo = response.Envelope.ReplyTo,
                SessionId = response.Envelope.SessionId,
                SentAt = response.Envelope.SentAt,
                Capability = response.Envelope.Capability,
                Payload = filteredPayload
            }
        };
    }

    Task<AppToolBridgeResponse> RefreshToolsFilteredAsync(
        string sessionId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => QueryToolsFilteredAsync(
            sessionId,
            arguments,
            cancellationToken,
            requestContext);

    Task<AppToolBridgeResponse> CallToolWithCatalogRecoveryAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => AppToolCallRecovery.ExecuteAsync(
            this,
            sessionId,
            toolId,
            arguments,
            after,
            queryBeforeCall: false,
            cancellationToken,
            requestContext);

    Task<AppToolBridgeResponse> CallToolWithCatalogPreflightAndRecoveryAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => AppToolCallRecovery.ExecuteAsync(
            this,
            sessionId,
            toolId,
            arguments,
            after,
            queryBeforeCall: true,
            cancellationToken,
            requestContext);

    Task<AppToolBridgeResponse> CallToolWithEvidenceAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => after is null
            ? CallToolAsync(sessionId, toolId, arguments, cancellationToken, requestContext)
            : Task.FromResult(AppToolBridgeResponse.FromFailure(
                "This app-tool bridge does not support post-call evidence."));

    Task<AppToolBridgeResponse> CallToolsAsync(
        string sessionId,
        IReadOnlyList<AppToolBatchCall> calls,
        bool continueOnError,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => Task.FromResult(AppToolBridgeResponse.FromFailure(
            "This app-tool bridge does not support batched calls."));
}
