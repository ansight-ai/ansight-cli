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
using Ansight.Infrastructure.Preferences;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using HostOperationResult = Ansight.Host.Runtime.Contracts.OperationResult;
using static WebSocketSessionProtocol;
using static WebSocketTransport;

internal sealed partial class WebSocketSessionManager
{
    private async Task RunSessionServerAsync(
        string sessionId,
        WebSocketSessionInfo session,
        PairingSessionAuthorization authorization,
        TimeSpan maxLifetime,
        TcpListener listener,
        TaskCompletionSource startedSignal)
    {
        startedSignal.TrySetResult();

        using var lifetimeCts = new CancellationTokenSource(maxLifetime);

        try
        {
            await using var connection = await WebSocketAcceptor.AcceptAsync(
                listener,
                session.Path,
                session.Token,
                lifetimeCts.Token);
            listener.Stop();
            await RunConnectedSessionAsync(
                sessionId,
                connection.WebSocket,
                authorization);
        }
        catch (OperationCanceledException)
        {
            runtimeState.SetSessionStatus(
                sessionId,
                "WebSocket Timeout",
                "WebSocket session timed out before client completed exchange.");
        }
        catch (SocketException ex)
        {
            runtimeState.SetSessionStatus(sessionId, "WebSocket Error", $"WebSocket listener error: {ex.Message}");
        }
        catch (Exception ex)
        {
            runtimeState.SetSessionStatus(sessionId, "WebSocket Error", $"WebSocket session error: {ex.Message}");
        }
        finally
        {
            listener.Stop();
            portLeasePool.Release(session.Port);
        }
    }

    private async Task<bool> RunConnectedSessionAsync(
        string sessionId,
        WebSocket socket,
        PairingSessionAuthorization authorization)
    {
        var telemetrySegmentId = runtimeState.BeginSessionConnection(sessionId);
        var liveConnection = new LiveSessionConnection(sessionId, socket, authorization);
        RegisterConnection(sessionId, liveConnection);

        try
        {
            runtimeState.SetSessionStatus(sessionId, "WebSocket Open", "WebSocket handshake accepted.");
            PublishSessionCaptureEvent(
                RuntimeSessionCaptureEventKind.Started,
                sessionId,
                status: "WebSocket Open",
                message: "WebSocket handshake accepted.");
            runtimeState.AddSessionLog(sessionId, CreateLogEntry("Awaiting client control requests.", "WebSocket"));
            CaptureSessionAppToolCatalogAsync(sessionId).SafeFireAndForget();

            while (socket.State == WebSocketState.Open)
            {
                var incoming = await ReceiveMessageAsync(socket, CancellationToken.None);
                if (incoming is null)
                {
                    if (liveConnection.IsDisconnectRequested)
                    {
                        return true;
                    }

                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry("Client closed WebSocket.", "Connection"));
                    break;
                }

                ParsedClientEvent parsedEvent;
                if (incoming.MessageType == WebSocketMessageType.Text
                    && TryHandleToolBridgeResponse(sessionId, incoming.TextPayload ?? string.Empty))
                {
                    continue;
                }

                if (incoming.MessageType == WebSocketMessageType.Text
                    && TryParseControlRequest(incoming.TextPayload ?? string.Empty, out var controlRequest)
                    && controlRequest is not null)
                {
                    var controlResponse = await HandleControlRequestAsync(sessionId, controlRequest);
                    await SendJsonAsync(socket, controlResponse, CancellationToken.None);
                    runtimeState.AddSessionLog(sessionId, CreateLogEntry($"Sent control response for {controlRequest.Action}.", "WebSocket"));
                    if (string.Equals(controlRequest.Action, PairingControlActions.SessionComplete, StringComparison.Ordinal))
                    {
                        break;
                    }

                    continue;
                }

                if (incoming.MessageType == WebSocketMessageType.Binary
                    && annotatedFeedbackTransfers.TryHandleBinaryMessage(
                        sessionId,
                        incoming.BinaryPayload,
                        out var annotatedFeedbackCompletion))
                {
                    if (annotatedFeedbackCompletion is not null)
                    {
                        var importResult = await AnnotatedFeedbackSessionImporter.ImportAsync(
                            runtimeState,
                            annotatedFeedbackCompletion);
                        runtimeState.AddSessionLog(
                            sessionId,
                            CreateLogEntry(
                                importResult.Message,
                                "Annotated Feedback",
                                importResult.IsSuccess ? LogPriority.Information : LogPriority.Warning));
                        if (importResult.IsSuccess)
                        {
                            PublishSessionTransferEvent(
                                sessionId,
                                RuntimeSessionTransferKind.AnnotatedFeedback,
                                itemCount: 1,
                                message: "Received annotated feedback from the app.",
                                annotatedFeedbackCompletion.CapturedAtUtc);
                        }
                    }

                    continue;
                }

                if (incoming.MessageType == WebSocketMessageType.Binary
                    && binaryToolArtifactTransfers.TryHandleBinaryMessage(sessionId, incoming.BinaryPayload))
                {
                    continue;
                }

                parsedEvent = incoming.MessageType == WebSocketMessageType.Binary
                    ? ClientEventParser.ParseBinary(incoming.BinaryPayload)
                    : ClientEventParser.ParseText(incoming.TextPayload ?? string.Empty);
                if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientLog, StringComparison.Ordinal))
                {
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry(parsedEvent.Data ?? "<empty>", WebSocketClientEventConstants.ClientLog));
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.Log,
                        itemCount: 1,
                        message: "Received client log entry.");
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientDone, StringComparison.Ordinal))
                {
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry("Client marked log stream complete.", "Stream"));
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientMetricChannels, StringComparison.Ordinal))
                {
                    runtimeState.UpdateSessionMetricChannels(sessionId, parsedEvent.MetricChannels);
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry($"Registered {parsedEvent.MetricChannels.Count} metric channels.", "Metrics"));
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.Telemetry,
                        parsedEvent.MetricChannels.Count,
                        $"Registered {parsedEvent.MetricChannels.Count} metric channel(s).");
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientMetrics, StringComparison.Ordinal))
                {
                    runtimeState.AddSessionMetrics(sessionId, parsedEvent.Metrics, telemetrySegmentId);
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.Telemetry,
                        parsedEvent.Metrics.Count,
                        $"Received {parsedEvent.Metrics.Count} telemetry sample(s).",
                        parsedEvent.Metrics.Count == 0
                            ? null
                            : parsedEvent.Metrics.Max(metric => metric.CapturedAtUtc));
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientEvents, StringComparison.Ordinal))
                {
                    runtimeState.AddSessionApplicationEvents(sessionId, parsedEvent.Events);
                    runtimeState.AddSessionLogs(
                        sessionId,
                        parsedEvent.Events.Select(CreateClientEventLog).ToArray());
                    foreach (var appEvent in parsedEvent.Events)
                    {
                        PublishAppEvent(sessionId, appEvent);
                    }

                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.AppEvent,
                        parsedEvent.Events.Count,
                        $"Received {parsedEvent.Events.Count} app event(s).",
                        parsedEvent.Events.Count == 0
                            ? null
                            : parsedEvent.Events.Max(@event => @event.CapturedAtUtc));
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientTouchInput, StringComparison.Ordinal))
                {
                    runtimeState.AddSessionTouches(sessionId, parsedEvent.Touches);
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.TouchInput,
                        parsedEvent.Touches.Count,
                        $"Received {parsedEvent.Touches.Count} touch input record(s).",
                        parsedEvent.Touches.Count == 0
                            ? null
                            : parsedEvent.Touches.Max(touch => touch.CapturedAtUtc));
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientNetworkRequest, StringComparison.Ordinal)
                         && parsedEvent.NetworkRequest is not null)
                {
                    runtimeState.AddSessionNetworkRequests(sessionId, [parsedEvent.NetworkRequest]);
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.AppEvent,
                        1,
                        $"Received {parsedEvent.NetworkRequest.Method} network request.",
                        parsedEvent.NetworkRequest.CompletedAtUtc);
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientAppState, StringComparison.Ordinal) && parsedEvent.AppState is not null)
                {
                    runtimeState.SetSessionAppState(sessionId, parsedEvent.AppState.Value, parsedEvent.AppStateChangedUtc);
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.DeviceAppProfile, StringComparison.Ordinal))
                {
                    runtimeState.SetSessionDeviceProfile(sessionId, parsedEvent.DeviceProfile, parsedEvent.DeviceProfileJson);
                    sdkIosInstrumentsCaptureManager?.Attach(
                        sessionId, parsedEvent.DeviceProfile, parsedEvent.DeviceProfileJson);
                    if (nativeSessionLogCaptureManager is not null)
                    {
                        nativeSessionLogCaptureManager.AttachAsync(
                            sessionId,
                            parsedEvent.DeviceProfile,
                            parsedEvent.DeviceProfileJson)
                            .SafeFireAndForget();
                    }

                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry("Received device/app properties payload.", WebSocketClientEventConstants.DeviceAppProfile));
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.AppProfile,
                        itemCount: 1,
                        message: "Received device/app profile payload.");
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientJpeg, StringComparison.Ordinal) && parsedEvent.ImageFrame is not null)
                {
                    runtimeState.AddSessionImage(
                        sessionId,
                        parsedEvent.ImageFrame.CapturedAtUtc,
                        parsedEvent.ImageFrame.Format,
                        parsedEvent.ImageFrame.Width,
                        parsedEvent.ImageFrame.Height,
                        parsedEvent.ImageFrame.Quality,
                        parsedEvent.ImageFrame.Bytes);
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.Screenshot,
                        itemCount: 1,
                        message: $"Received screenshot frame {parsedEvent.ImageFrame.Width}x{parsedEvent.ImageFrame.Height}.",
                        parsedEvent.ImageFrame.CapturedAtUtc);
                }
                else if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientVisualTree, StringComparison.Ordinal) && parsedEvent.VisualTreeSnapshot is not null)
                {
                    var ingestionResult = runtimeState.ReceiveSessionVisualTreeSnapshot(sessionId, parsedEvent.VisualTreeSnapshot);
                    if (ingestionResult.WasSaved)
                    {
                        PublishSessionTransferEvent(
                            sessionId,
                            RuntimeSessionTransferKind.VisualTree,
                            itemCount: 1,
                            message: $"Received visual tree with {parsedEvent.VisualTreeSnapshot.NodeCount} node(s).",
                            parsedEvent.VisualTreeSnapshot.CapturedAtUtc);
                    }
                    else if (!ingestionResult.Result.IsSuccess)
                    {
                        runtimeState.AddSessionLog(
                            sessionId,
                            CreateClientLogEntry(
                                ingestionResult.Result.Message,
                                WebSocketClientEventConstants.ClientVisualTree));
                    }
                }
                else
                {
                    var payloadSummary = incoming.MessageType == WebSocketMessageType.Binary
                        ? $"<binary:{incoming.BinaryPayload.Length} bytes>"
                        : incoming.TextPayload ?? string.Empty;
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry($"Received client payload: {payloadSummary}", parsedEvent.Type ?? "Payload"));
                }

                if (string.Equals(parsedEvent.Type, WebSocketClientEventConstants.ClientDone, StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (liveConnection.IsDisconnectRequested)
            {
                return true;
            }

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "WebSocket session complete.", CancellationToken.None);
            }

            runtimeState.SetSessionStatus(sessionId, "WebSocket Complete", "WebSocket session completed.");
            return true;
        }
        catch (Exception ex) when (liveConnection.IsDisconnectRequested && IsExpectedForcedDisconnectException(ex))
        {
            return true;
        }
        finally
        {
            UnregisterConnection(
                sessionId,
                liveConnection,
                liveConnection.DisconnectReason ?? "Live app connection closed.");
        }
    }

}


internal sealed partial class WebSocketSessionManager
{

    private void DisconnectAllConnections(string reason)
    {
        var connections = connectionRegistry.GetConnections();

        foreach (var connection in connections)
        {
            connection.RequestDisconnect(reason);
            try
            {
                connection.Abort();
            }
            catch (Exception ex) when (IsExpectedForcedDisconnectException(ex))
            {
                log.Info($"session_auth_disconnect_abort_ignored sessionId={connection.SessionId} message={ex.Message}");
            }

            UnregisterConnection(connection.SessionId, connection, reason);
        }
    }

    private bool TryParseControlRequest(string payload, out PairingControlEnvelope? envelope)
    {
        envelope = null;

        try
        {
            envelope = JsonSerializer.Deserialize<PairingControlEnvelope>(payload, PairingJson.Compact);
        }
        catch (JsonException)
        {
            return false;
        }

        return envelope is not null &&
               string.Equals(envelope.Type, PairingControlEnvelope.RequestType, StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(envelope.Id) &&
               !string.IsNullOrWhiteSpace(envelope.Action);
    }

    private async Task<PairingControlEnvelope> HandleControlRequestAsync(
        string sessionId,
        PairingControlEnvelope request)
    {
        try
        {
            switch (request.Action)
            {
                case PairingControlActions.SessionOpen:
                {
                    var clientName = request.Payload?["clientName"]?.GetValue<string>()?.Trim();
                    var configId = request.Payload?["configId"]?.GetValue<string>()?.Trim();
                    if (!TryParseCustomProperties(request.Payload, requireCustomProperties: false, out var customProperties, out var customPropertiesError))
                    {
                        return CreateControlResponse(request, success: false, customPropertiesError ?? "Invalid session properties payload.");
                    }

                    if (request.Payload?.ContainsKey("customProperties") == true)
                    {
                        runtimeState.SetSessionCustomProperties(sessionId, customProperties);
                    }

                    var message = string.IsNullOrWhiteSpace(clientName)
                        ? "Session open accepted."
                        : $"Session open accepted for {clientName}.";
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry(
                        string.IsNullOrWhiteSpace(configId)
                            ? message
                            : $"{message} configId={configId}",
                        "Session"));
                    return CreateControlResponse(request, success: true, message);
                }
                case PairingControlActions.SessionProperties:
                {
                    if (!TryParseCustomProperties(request.Payload, requireCustomProperties: true, out var customProperties, out var error))
                    {
                        return CreateControlResponse(request, success: false, error ?? "Invalid session properties payload.");
                    }

                    runtimeState.SetSessionCustomProperties(sessionId, customProperties);
                    return CreateControlResponse(request, success: true, "Session properties received.");
                }
                case PairingControlActions.ClientLog:
                {
                    var logLine = request.Payload?["data"]?.GetValue<string>() ?? "<empty>";
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry(logLine, WebSocketClientEventConstants.ClientLog));
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.Log,
                        itemCount: 1,
                        message: "Received client log entry.");
                    return CreateControlResponse(request, success: true, "Client log received.");
                }
                case PairingControlActions.DeviceProfile:
                {
                    var profileJson = request.Payload?.ToJsonString(JsonUtil.Pretty);
                    var compactProfileJson = request.Payload?.ToJsonString(JsonUtil.Compact);
                    DeviceAppProfile? deviceProfile = null;
                    if (!string.IsNullOrWhiteSpace(compactProfileJson))
                    {
                        try
                        {
                            deviceProfile = JsonSerializer.Deserialize<DeviceAppProfile>(compactProfileJson, JsonUtil.Compact);
                        }
                        catch (JsonException ex)
                        {
                            return CreateControlResponse(request, success: false, $"Invalid device profile payload: {ex.Message}");
                        }
                    }

                    runtimeState.SetSessionDeviceProfile(sessionId, deviceProfile, profileJson);
                    sdkIosInstrumentsCaptureManager?.Attach(sessionId, deviceProfile, profileJson);
                    if (nativeSessionLogCaptureManager is not null)
                    {
                        nativeSessionLogCaptureManager.AttachAsync(sessionId, deviceProfile, profileJson).SafeFireAndForget();
                    }

                    var supportsHostCapturePolicy =
                        ExternalSessionScreenshotCapturePolicy.SupportsHostControl(request.Payload);
                    var screenshotCaptureRequest =
                        ExternalSessionScreenshotCaptureRequest.FromPayload(request.Payload);
                    var capturePolicy = !supportsHostCapturePolicy
                        ? ExternalSessionScreenshotCapturePolicy.App("The SDK does not support host-managed screenshot capture.")
                        : externalSessionScreenshotCaptureManager is null
                            ? ExternalSessionScreenshotCapturePolicy.App("Host external screenshot capture is unavailable.")
                            : await externalSessionScreenshotCaptureManager
                                .AttachAsync(sessionId, deviceProfile, profileJson, screenshotCaptureRequest)
                                .ConfigureAwait(false);
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry("Received device/app properties payload.", WebSocketClientEventConstants.DeviceAppProfile));
                    PublishSessionTransferEvent(
                        sessionId,
                        RuntimeSessionTransferKind.AppProfile,
                        itemCount: 1,
                        message: "Received device/app profile payload.");
                    return CreateControlResponse(
                        request,
                        success: true,
                        "Device profile received.",
                        capturePolicy.ToResponsePayload());
                }
                case PairingControlActions.AppState:
                {
                    var appState = ParseControlAppLifecycleState(request.Payload);
                    if (appState is null)
                    {
                        return CreateControlResponse(request, success: false, "App state payload was invalid.");
                    }

                    var changedAtUtc = ParseControlChangedAtUtc(request.Payload);
                    runtimeState.SetSessionAppState(sessionId, appState.Value, changedAtUtc);
                    return CreateControlResponse(request, success: true, "App state received.");
                }
                case PairingControlActions.SessionComplete:
                {
                    runtimeState.AddSessionLog(sessionId, CreateClientLogEntry("Client marked session complete.", "Stream"));
                    return CreateControlResponse(request, success: true, "Session complete acknowledged.");
                }
                case CrashReportReceiver.HandoffAction:
                {
                    var result = crashReportReceiver?.Receive(sessionId, request.Payload)
                        ?? HostOperationResult.Failure("Crash report storage is unavailable.");
                    return CreateControlResponse(request, result.IsSuccess, result.Message);
                }
                case AnnotatedFeedbackTransferManager.SubmitAction:
                {
                    var accepted = annotatedFeedbackTransfers.TryRegister(
                        sessionId,
                        request.Payload,
                        out var message);
                    return CreateControlResponse(request, accepted, message);
                }
                default:
                    return CreateControlResponse(request, success: false, $"Unsupported control action '{request.Action}'.");
            }
        }
        catch (Exception ex)
        {
            return CreateControlResponse(request, success: false, ex.Message);
        }
    }

    private static PairingControlEnvelope CreateControlResponse(
        PairingControlEnvelope request,
        bool success,
        string message,
        JsonObject? payload = null)
    {
        return new PairingControlEnvelope
        {
            Type = PairingControlEnvelope.ResponseType,
            Action = request.Action,
            ReplyTo = request.Id,
            Success = success,
            Message = message,
            Payload = payload
        };
    }

    private void ExternalSessionScreenshotCaptureManagerOnCaptureFailed(
        object? sender,
        ExternalSessionScreenshotCaptureFailedEventArgs args)
    {
        runtimeState.AddSessionLog(
            args.SessionId,
            CreateLogEntry(args.Reason, "External Screenshot", LogPriority.Warning));
        Task.Run(() => ForceDisconnectSession(args.SessionId)).SafeFireAndForget();
    }

}
