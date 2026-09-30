namespace Ansight.Host.Runtime.WebSocketSessions;

using System.Buffers;
using System.Globalization;
using System.Text;
using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Infrastructure.Preferences;
using Ansight.Infrastructure.Utilities;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using HostOperationResult = Ansight.Host.Runtime.Contracts.OperationResult;
using static WebSocketTransport;

[Export]
[Export(typeof(IAppToolBridge))]
internal sealed partial class WebSocketSessionManager : IAppToolBridge
{
    private static readonly Ansight.Infrastructure.Logging.ILogger log = Ansight.Infrastructure.Logging.Logger.Create();
    private readonly IRuntimeState runtimeState;
    private readonly SessionPortLeasePool portLeasePool = new();
    private readonly SessionConnectionRegistry connectionRegistry = new();
    private readonly AppToolCatalogService toolCatalog;
    private readonly BinaryToolArtifactTransferManager binaryToolArtifactTransfers = new();
    private readonly AnnotatedFeedbackTransferManager annotatedFeedbackTransfers = new();
    private readonly IHostOperationTrafficLog trafficAuditLog;
    private readonly IUserPreferences? userPreferences;
    private readonly ISimulatorCrashReportCollector? simulatorCrashReportCollector;
    private readonly INativeSessionLogCaptureManager? nativeSessionLogCaptureManager;
    private readonly IExternalSessionScreenshotCaptureManager? externalSessionScreenshotCaptureManager;
    private readonly SdkIosInstrumentsCaptureManager? sdkIosInstrumentsCaptureManager;
    private readonly CrashReportReceiver? crashReportReceiver;

    [ImportingConstructor]
    public WebSocketSessionManager(
        IRuntimeState runtimeState,
        IHostOperationTrafficLog trafficAuditLog,
        [Import(AllowDefault = true)] IUserPreferences? userPreferences = null,
        [Import(AllowDefault = true)] ISimulatorCrashReportCollector? simulatorCrashReportCollector = null,
        [Import(AllowDefault = true)] INativeSessionLogCaptureManager? nativeSessionLogCaptureManager = null,
        [Import(AllowDefault = true)] IExternalSessionScreenshotCaptureManager? externalSessionScreenshotCaptureManager = null,
        [Import(AllowDefault = true)] CrashReportReceiver? crashReportReceiver = null,
        [Import(AllowDefault = true)] SdkIosInstrumentsCaptureManager? sdkIosInstrumentsCaptureManager = null)
    {
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.trafficAuditLog = trafficAuditLog ?? throw new ArgumentNullException(nameof(trafficAuditLog));
        this.userPreferences = userPreferences;
        this.simulatorCrashReportCollector = simulatorCrashReportCollector;
        this.nativeSessionLogCaptureManager = nativeSessionLogCaptureManager;
        this.externalSessionScreenshotCaptureManager = externalSessionScreenshotCaptureManager;
        this.crashReportReceiver = crashReportReceiver;
        this.sdkIosInstrumentsCaptureManager = sdkIosInstrumentsCaptureManager;
        toolCatalog = new AppToolCatalogService(
            connectionRegistry,
            SendToolRequestAsync);
        if (this.externalSessionScreenshotCaptureManager is not null)
        {
            this.externalSessionScreenshotCaptureManager.CaptureFailed += ExternalSessionScreenshotCaptureManagerOnCaptureFailed;
        }
    }

    public event EventHandler? ConnectionsChanged;

    public async Task<WebSocketSessionInfo?> IssueSessionAsync(
        string sessionId,
        PairingSessionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        if (!portLeasePool.TryLeaseListener(out var leasedPort, out var listener, out var leaseError))
        {
            runtimeState.SetSessionStatus(sessionId, "WebSocket Error", leaseError);
            return null;
        }

        var session = new WebSocketSessionInfo(
            leasedPort,
            ProtocolDefaults.WebSocketPath,
            CryptoUtil.CreateBase64UrlRandom(24));

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task.Run(
            () => RunSessionServerAsync(
                sessionId,
                session,
                authorization,
                TimeSpan.FromMinutes(2),
                listener,
                startedTcs))
            .SafeFireAndForget();

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await WaitForSessionStartupAsync(startedTcs.Task, startupTimeout.Token);
            runtimeState.SetSessionStatus(
                sessionId,
                "Awaiting WebSocket",
                $"Issued WebSocket endpoint on port {session.Port} path {session.Path}.");

            return session;
        }
        catch (Exception ex)
        {
            var statusMessage = ListenerPortWarning.TryCreateMessage(
                ex,
                "WebSocket session listener",
                $"TCP port {session.Port}",
                out var warning)
                ? warning
                : $"WebSocket session startup failed: {ex.Message}";
            runtimeState.SetSessionStatus(sessionId, "WebSocket Error", statusMessage);
            return null;
        }
    }

    public IReadOnlyList<string> GetConnectedSessionIds()
        => connectionRegistry.GetSessionIds();

    public bool IsSessionConnected(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        return connectionRegistry.Contains(sessionId.Trim());
    }

    public HostOperationResult ForceDisconnectSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return HostOperationResult.Failure("Session ID is required.");
        }

        var normalizedSessionId = sessionId.Trim();
        var connection = connectionRegistry.Get(normalizedSessionId);

        if (connection is null)
        {
            return HostOperationResult.Failure($"Session '{normalizedSessionId}' is not currently connected.");
        }

        const string disconnectReason = "Force disconnected from the host.";
        connection.RequestDisconnect(disconnectReason);
        try
        {
            connection.Abort();
        }
        catch (Exception ex) when (IsExpectedForcedDisconnectException(ex))
        {
            log.Info($"session_force_disconnect_abort_ignored sessionId={normalizedSessionId} message={ex.Message}");
        }

        UnregisterConnection(normalizedSessionId, connection, disconnectReason);

        log.Info($"session_force_disconnect_requested sessionId={normalizedSessionId}");
        return HostOperationResult.Success($"Force disconnected session '{normalizedSessionId}'.");
    }

    public async Task<AppToolBridgeResponse> QueryToolsAsync(
        string sessionId,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
    {
        var response = await toolCatalog.QueryAsync(
                sessionId,
                filterArguments: null,
                callerRevision: null,
                cancellationToken,
                requestContext)
            .ConfigureAwait(false);
        CaptureQueriedAppToolCatalog(sessionId, response);
        return response;
    }

    public Task<AppToolBridgeResponse> QueryToolsSinceAsync(
        string sessionId,
        string? ifRevision,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => toolCatalog.QueryAsync(
            sessionId,
            filterArguments: null,
            callerRevision: ifRevision,
            cancellationToken,
            requestContext);

    public Task<AppToolBridgeResponse> QueryToolsFilteredAsync(
        string sessionId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => toolCatalog.QueryAsync(
            sessionId,
            arguments,
            arguments?["ifRevision"]?.GetValue<string>(),
            cancellationToken,
            requestContext);

    public Task<AppToolBridgeResponse> RefreshToolsFilteredAsync(
        string sessionId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => toolCatalog.QueryAsync(
            sessionId,
            arguments,
            callerRevision: null,
            cancellationToken,
            requestContext,
            forceRefresh: true);

    public Task<AppToolBridgeResponse> CallToolAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
        => CallToolWithEvidenceAsync(
            sessionId,
            toolId,
            arguments,
            after: null,
            cancellationToken,
            requestContext);

    public Task<AppToolBridgeResponse> CallToolWithEvidenceAsync(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return Task.FromResult(AppToolBridgeResponse.FromFailure("Tool ID is required."));
        }

        var envelope = CreateCallToolEnvelope(sessionId, toolId, arguments, after);

        return SendToolRequestAsync(sessionId, envelope, cancellationToken, requestContext);
    }

    internal static ToolProtocolEnvelope CreateCallToolEnvelope(
        string sessionId,
        string toolId,
        JsonObject? arguments,
        JsonObject? after)
    {
        return new ToolProtocolEnvelope
        {
            Type = ToolProtocolMessageTypes.CallType,
            Id = CreateToolRequestId("call"),
            SessionId = sessionId,
            Capability = ToolProtocolMessageTypes.Capability,
            Payload = new JsonObject
            {
                ["toolId"] = toolId.Trim(),
                ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
                ["after"] = after?.DeepClone()
            }
        };
    }

    public Task<AppToolBridgeResponse> CallToolsAsync(
        string sessionId,
        IReadOnlyList<AppToolBatchCall> calls,
        bool continueOnError,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext = null)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Count is < 1 or > 32)
        {
            return Task.FromResult(AppToolBridgeResponse.FromFailure(
                "A tool batch must contain between 1 and 32 calls."));
        }

        var callPayloads = new JsonArray();
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.ToolId))
            {
                return Task.FromResult(AppToolBridgeResponse.FromFailure(
                    "Every batched call requires a tool ID."));
            }

            callPayloads.Add(new JsonObject
            {
                ["callId"] = call.CallId,
                ["toolId"] = call.ToolId.Trim(),
                ["arguments"] = call.Arguments?.DeepClone() ?? new JsonObject(),
                ["after"] = call.After?.DeepClone()
            });
        }

        var envelope = new ToolProtocolEnvelope
        {
            Type = AppToolProtocolContracts.BatchType,
            Id = CreateToolRequestId("batch"),
            SessionId = sessionId,
            Capability = ToolProtocolMessageTypes.Capability,
            Payload = new JsonObject
            {
                ["continueOnError"] = continueOnError,
                ["calls"] = callPayloads
            }
        };

        return SendToolRequestAsync(sessionId, envelope, cancellationToken, requestContext);
    }

}
