using System.Text.Json.Nodes;

namespace Ansight.Host.Apps;

public sealed class AppToolService
{
    private readonly IAppToolBridge appToolBridge;
    private readonly SessionActivityService? activity;
    private readonly Func<string, bool>? isExternalSession;

    internal AppToolService(IAppToolBridge appToolBridge, Func<string, bool>? isExternalSession = null, SessionActivityService? activity = null)
    {
        this.activity = activity;
        this.isExternalSession = isExternalSession;
        this.appToolBridge = appToolBridge ?? throw new ArgumentNullException(nameof(appToolBridge));
    }

    public IReadOnlyList<string> GetConnectedSessionIds()
        => appToolBridge.GetConnectedSessionIds();

    public bool IsConnected(string sessionId)
        => appToolBridge.IsSessionConnected(sessionId);

    public OperationResult ForceDisconnect(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return OperationResult.Failure("Session ID is required.");
        }

        return activity is null ? appToolBridge.ForceDisconnectSession(sessionId.Trim())
            : activity.Record("session.disconnect", "session", sessionId,
                () => appToolBridge.ForceDisconnectSession(sessionId.Trim()), result => result.IsSuccess ? "succeeded" : "failed");
    }

    public Task<RuntimeAppToolResponse> QueryAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return QueryCoreAsync(
            sessionId.Trim(),
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "query_app_tools"));
    }

    internal Task<RuntimeAppToolResponse> QueryForLocalExplorerAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return QueryCoreAsync(
            sessionId.Trim(),
            cancellationToken,
            new AppToolBridgeRequestContext("local-explorer", "query_app_graph_navigation_tools"));
    }

    internal Task<RuntimeAppToolResponse> QueryForTaskAuthoringAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return QueryCoreAsync(
            sessionId.Trim(),
            cancellationToken,
            new AppToolBridgeRequestContext("local-explorer", "query_task_authoring_tools"));
    }

    public Task<RuntimeAppToolResponse> QuerySinceAsync(
        string sessionId,
        string? ifRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return QuerySinceCoreAsync(
            sessionId.Trim(),
            ifRevision,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "query_app_tools"));
    }

    public Task<RuntimeAppToolResponse> QueryFilteredAsync(
        string sessionId,
        JsonObject filters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(filters);
        return QueryFilteredCoreAsync(
            sessionId.Trim(),
            filters,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "query_app_tools"));
    }

    public Task<RuntimeAppToolResponse> QueryForCompanionAsync(
        string sessionId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return QueryCoreAsync(
            sessionId.Trim(),
            cancellationToken,
            new AppToolBridgeRequestContext(
                "companion",
                "query_app_tools",
                correlationId.Trim()));
    }

    public Task<RuntimeAppToolResponse> CallAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return CallCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "call_app_tool"));
    }

    internal Task<RuntimeAppToolResponse> CallForLocalExplorerAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return CallCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext("local-explorer", "observe_app_graph_navigation"));
    }

    internal Task<RuntimeAppToolResponse> CallForTaskAuthoringAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return CallCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext("local-explorer", "query_task_authoring_artifacts"));
    }

    public Task<RuntimeAppToolResponse> CallWithEvidenceAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject after,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentNullException.ThrowIfNull(after);
        return CallWithEvidenceCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            after,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "call_app_tool_with_evidence"));
    }

    public Task<RuntimeAppToolResponse> CallBatchAsync(
        string sessionId,
        IReadOnlyList<RuntimeAppToolBatchCall> calls,
        bool continueOnError = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(calls);
        return CallBatchCoreAsync(
            sessionId.Trim(),
            calls,
            continueOnError,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "call_app_tools"));
    }

    public Task<RuntimeAppToolResponse> CallWithCatalogAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return CallWithCatalogCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext("local-player", "call_app_tool"));
    }

    public Task<RuntimeAppToolResponse> CallForCompanionAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return CallCoreAsync(
            sessionId.Trim(),
            toolId.Trim(),
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext(
                "companion",
                "call_app_tool",
                correlationId.Trim()));
    }

    public Task<RuntimeAppToolResponse> PushFileAsync(
        string sessionId,
        string localFilePath,
        string directoryPath,
        string? sandboxRoot = null,
        string? fileName = null,
        bool overwrite = false,
        bool createDirectory = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(localFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var request = new AppFilePushRequest(
            localFilePath.Trim(),
            directoryPath.Trim(),
            string.IsNullOrWhiteSpace(sandboxRoot) ? null : sandboxRoot.Trim(),
            string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim(),
            overwrite,
            createDirectory);

        return PushFileCoreAsync(sessionId.Trim(), request, cancellationToken);
    }

    private async Task<RuntimeAppToolResponse> QueryCoreAsyncUnrecorded(
        string sessionId,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.QueryToolsAsync(
            sessionId,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> QuerySinceCoreAsyncUnrecorded(
        string sessionId,
        string? ifRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.QueryToolsSinceAsync(
            sessionId,
            ifRevision,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> QueryFilteredCoreAsyncUnrecorded(
        string sessionId,
        JsonObject filters,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.QueryToolsFilteredAsync(
            sessionId,
            filters,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> CallCoreAsyncUnrecorded(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.CallToolAsync(
            sessionId,
            toolId,
            arguments,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> CallWithEvidenceCoreAsyncUnrecorded(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.CallToolWithEvidenceAsync(
            sessionId,
            toolId,
            arguments,
            after,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> CallBatchCoreAsyncUnrecorded(
        string sessionId,
        IReadOnlyList<RuntimeAppToolBatchCall> calls,
        bool continueOnError,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var bridgeCalls = calls.Select(static call => new AppToolBatchCall(
            call.ToolId,
            call.Arguments?.DeepClone().AsObject(),
            call.After?.DeepClone().AsObject(),
            call.CallId)).ToArray();
        var response = await appToolBridge.CallToolsAsync(
            sessionId,
            bridgeCalls,
            continueOnError,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> CallWithCatalogCoreAsyncUnrecorded(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        var response = await appToolBridge.CallToolWithCatalogRecoveryAsync(
            sessionId,
            toolId,
            arguments?.DeepClone().AsObject(),
            after: null,
            cancellationToken,
            requestContext).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private async Task<RuntimeAppToolResponse> PushFileCoreAsyncUnrecorded(
        string sessionId,
        AppFilePushRequest request,
        CancellationToken cancellationToken)
    {
        if (isExternalSession?.Invoke(sessionId) == true) return SdkRequired(sessionId);

        JsonObject arguments;
        try
        {
            arguments = await AppFilePush.CreateRemoteArgumentsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new RuntimeAppToolResponse(false, exception.Message, null);
        }

        var response = await appToolBridge.CallToolAsync(
            sessionId,
            AppFilePush.ToolId,
            arguments,
            cancellationToken,
            new AppToolBridgeRequestContext("ansight", "push_file_to_app")).ConfigureAwait(false);
        return CreateResponse(response);
    }

    private Task<RuntimeAppToolResponse> QueryCoreAsync(
        string sessionId,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? QueryCoreAsyncUnrecorded(sessionId, cancellationToken, requestContext)
            : activity.RecordAsync("app.query", "app-tool", sessionId,
                () => QueryCoreAsyncUnrecorded(sessionId, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> QuerySinceCoreAsync(
        string sessionId,
        string? ifRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? QuerySinceCoreAsyncUnrecorded(sessionId, ifRevision, cancellationToken, requestContext)
            : activity.RecordAsync("app.querysince", "app-tool", sessionId,
                () => QuerySinceCoreAsyncUnrecorded(sessionId, ifRevision, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> QueryFilteredCoreAsync(
        string sessionId,
        JsonObject filters,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? QueryFilteredCoreAsyncUnrecorded(sessionId, filters, cancellationToken, requestContext)
            : activity.RecordAsync("app.queryfiltered", "app-tool", sessionId,
                () => QueryFilteredCoreAsyncUnrecorded(sessionId, filters, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> CallCoreAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? CallCoreAsyncUnrecorded(sessionId, toolId, arguments, cancellationToken, requestContext)
            : activity.RecordAsync(toolId, "app-tool", sessionId,
                () => CallCoreAsyncUnrecorded(sessionId, toolId, arguments, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> CallWithEvidenceCoreAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? CallWithEvidenceCoreAsyncUnrecorded(sessionId, toolId, arguments, after, cancellationToken, requestContext)
            : activity.RecordAsync(toolId, "app-tool", sessionId,
                () => CallWithEvidenceCoreAsyncUnrecorded(sessionId, toolId, arguments, after, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> CallBatchCoreAsync(
        string sessionId,
        IReadOnlyList<RuntimeAppToolBatchCall> calls,
        bool continueOnError,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? CallBatchCoreAsyncUnrecorded(sessionId, calls, continueOnError, cancellationToken, requestContext)
            : activity.RecordAsync("app.callbatch", "app-tool", sessionId,
                () => CallBatchCoreAsyncUnrecorded(sessionId, calls, continueOnError, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> CallWithCatalogCoreAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext requestContext)
        => activity is null ? CallWithCatalogCoreAsyncUnrecorded(sessionId, toolId, arguments, cancellationToken, requestContext)
            : activity.RecordAsync(toolId, "app-tool", sessionId,
                () => CallWithCatalogCoreAsyncUnrecorded(sessionId, toolId, arguments, cancellationToken, requestContext),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", requestContext.CorrelationId);

    private Task<RuntimeAppToolResponse> PushFileCoreAsync(
        string sessionId,
        AppFilePushRequest request,
        CancellationToken cancellationToken)
        => activity is null ? PushFileCoreAsyncUnrecorded(sessionId, request, cancellationToken)
            : activity.RecordAsync("app.pushfile", "app-tool", sessionId,
                () => PushFileCoreAsyncUnrecorded(sessionId, request, cancellationToken),
                result => result.Success && result.Envelope?.Type != Ansight.Tools.ToolProtocolMessageTypes.ErrorType ? "succeeded" : "failed", null);

    private static RuntimeAppToolResponse SdkRequired(string sessionId)
    {
        var detail = ExecutionCapabilities.Unavailable("app.tools",
            new JsonObject { ["executionMode"] = "device" }, "This capture uses external monitors.");
        return new RuntimeAppToolResponse(false, detail["message"]!.GetValue<string>(), new Ansight.Tools.ToolProtocolEnvelope
        {
            Type = Ansight.Tools.ToolProtocolMessageTypes.ErrorType, Id = Guid.NewGuid().ToString("N"), SessionId = sessionId, Payload = detail
        });
    }

    private static RuntimeAppToolResponse CreateResponse(AppToolBridgeResponse response)
        => new(response.Success, response.Message, response.Envelope, response.ArtifactSnapshotId);
}
