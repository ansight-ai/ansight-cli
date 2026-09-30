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

internal sealed partial class WebSocketSessionManager
{
    private async Task<AppToolBridgeResponse> SendToolRequestAsync(
        string sessionId,
        ToolProtocolEnvelope envelope,
        CancellationToken cancellationToken,
        AppToolBridgeRequestContext? requestContext)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? string.Empty
            : sessionId.Trim();
        var serializedRequest = SerializeEnvelope(envelope);

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            LogToolBridgeFailure(
                normalizedSessionId,
                envelope,
                serializedRequest,
                "Session ID is required.",
                requestContext,
                durationMs: 0);
            return AppToolBridgeResponse.FromFailure("Session ID is required.");
        }

        var connection = connectionRegistry.Get(normalizedSessionId);

        if (connection is null)
        {
            var message = $"Session '{normalizedSessionId}' is not currently connected.";
            LogToolBridgeFailure(
                normalizedSessionId,
                envelope,
                serializedRequest,
                message,
                requestContext,
                durationMs: 0);
            return AppToolBridgeResponse.FromFailure(message);
        }

        var toolId = TryReadToolId(envelope);
        string? authorizationError;
        bool isAuthorized;
        var requiresCatalogQuery = false;
        if (string.Equals(envelope.Type, ToolProtocolMessageTypes.QueryType, StringComparison.Ordinal))
        {
            isAuthorized = connection.CanQueryTools(out authorizationError);
        }
        else if (string.Equals(envelope.Type, AppToolProtocolContracts.BatchType, StringComparison.Ordinal))
        {
            isAuthorized = TryAuthorizeToolBatch(
                connection,
                envelope,
                out authorizationError,
                out requiresCatalogQuery);
        }
        else if (!string.IsNullOrWhiteSpace(toolId))
        {
            isAuthorized = connection.CanCallTool(
                toolId,
                out authorizationError,
                out requiresCatalogQuery);
        }
        else
        {
            isAuthorized = false;
            authorizationError = "Tool ID is required.";
        }
        if (!isAuthorized)
        {
            var message = authorizationError ?? "The authenticated client grant denied this tool operation.";
            LogToolBridgeFailure(
                normalizedSessionId,
                envelope,
                serializedRequest,
                message,
                requestContext,
                durationMs: 0);
            return AppToolBridgeResponse.FromFailure(message, requiresCatalogQuery);
        }

        LogToolBridgeRequest(normalizedSessionId, envelope, serializedRequest, requestContext);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ToolProtocolEnvelope? receivedResponse = null;

        try
        {
            var responseReceipt = await connection.SendRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
            var response = responseReceipt.Envelope;
            receivedResponse = response;
            var appResponseDurationMs = stopwatch.ElapsedMilliseconds;
            var binaryTransferMetrics = await AwaitBinaryToolArtifactTransferIfNeededAsync(normalizedSessionId, response, cancellationToken).ConfigureAwait(false);
            CaptureDiagnosticAppToolResult(normalizedSessionId, toolId, response);
            stopwatch.Stop();
            LogToolBridgeResponse(
                normalizedSessionId,
                envelope,
                response,
                serializedRequest,
                SerializeEnvelope(response),
                stopwatch.ElapsedMilliseconds,
                appResponseDurationMs,
                responseReceipt,
                binaryTransferMetrics,
                requestContext);
            return AppToolBridgeResponse.FromSuccess(
                $"Received {response.Type} from app session '{normalizedSessionId}'.",
                response,
                binaryTransferMetrics?.SnapshotId);
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            LogToolBridgeFailure(normalizedSessionId, envelope, serializedRequest, ex.Message, requestContext, stopwatch.ElapsedMilliseconds);
            return AppToolBridgeResponse.FromFailure(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            LogToolBridgeFailure(normalizedSessionId, envelope, serializedRequest, "The tool request was cancelled.", requestContext, stopwatch.ElapsedMilliseconds);
            return AppToolBridgeResponse.FromFailure("The tool request was cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogToolBridgeFailure(normalizedSessionId, envelope, serializedRequest, ex.Message, requestContext, stopwatch.ElapsedMilliseconds);
            return AppToolBridgeResponse.FromFailure($"Failed to proxy tool request to app session '{normalizedSessionId}': {ex.Message}");
        }
        finally
        {
            if (receivedResponse is not null)
            {
                binaryToolArtifactTransfers.CancelResponse(normalizedSessionId, receivedResponse,
                    "The app tool request ended before consuming its binary transfers.");
            }
        }
    }

    private void CaptureDiagnosticAppToolResult(
        string sessionId,
        string? toolId,
        ToolProtocolEnvelope response)
    {
        AppToolDiagnosticArtifactCaptureResult capture;
        try
        {
            capture = AppToolDiagnosticArtifactCapture.CaptureIfSupported(
                runtimeState,
                sessionId,
                toolId,
                response);
        }
        catch (Exception exception)
        {
            log.Info($"app_tool_diagnostic_artifact_failed sessionId={sessionId} toolId={toolId} message={FormatLogValue(exception.Message)}");
            return;
        }

        if (!capture.IsSupported)
        {
            return;
        }

        if (capture.IsCaptured)
        {
            log.Info($"app_tool_diagnostic_artifact_saved sessionId={sessionId} toolId={toolId} snapshotId={capture.SnapshotId}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(capture.Message))
        {
            log.Info($"app_tool_diagnostic_artifact_failed sessionId={sessionId} toolId={toolId} message={FormatLogValue(capture.Message)}");
        }
    }

    private async Task<BinaryToolArtifactTransferMetrics?> AwaitBinaryToolArtifactTransferIfNeededAsync(
        string sessionId,
        ToolProtocolEnvelope response,
        CancellationToken cancellationToken)
    {
        var registrations = binaryToolArtifactTransfers.TryGetCompletions(sessionId, response);
        if (registrations.Count == 0)
        {
            return null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var totalSizeBytes = 0L;
        string? lastSnapshotId = null;
        try
        {
            foreach (var registration in registrations)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
                    await registration.Completion.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    var sizeBytes = File.Exists(registration.ArtifactPath)
                        ? new FileInfo(registration.ArtifactPath).Length
                        : 0;
                    totalSizeBytes += sizeBytes;
                    if (registration.CaptureSessionArtifactSnapshot)
                    {
                        lastSnapshotId = CaptureCompletedBinaryToolArtifact(sessionId, registration, sizeBytes);
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for binary artifact transfer '{registration.TransferId}' from app session '{sessionId}'.",
                        ex);
                }
            }
        }
        catch (Exception exception)
        {
            // Cancellation or one failed batch item must close every still-active stream.
            foreach (var registration in registrations)
            {
                binaryToolArtifactTransfers.FailTransfer(sessionId, registration.TransferId, exception);
            }
            throw;
        }
        stopwatch.Stop();
        return new BinaryToolArtifactTransferMetrics(
            string.Join(",", registrations.Select(static registration => registration.TransferId)),
            stopwatch.ElapsedMilliseconds,
            totalSizeBytes,
            lastSnapshotId);
    }

    private string? CaptureCompletedBinaryToolArtifact(
        string sessionId,
        BinaryToolArtifactTransferRegistration registration,
        long sizeBytes)
    {
        if (!File.Exists(registration.ArtifactPath))
        {
            log.Info($"session_artifact_capture_skipped sessionId={sessionId} transferId={registration.TransferId} reason=missing_payload path={registration.ArtifactPath}");
            return null;
        }

        var capturedAtUtc = registration.CapturedAtUtc == default
            ? DateTimeOffset.UtcNow
            : registration.CapturedAtUtc.ToUniversalTime();
        var snapshotId = $"app-artifact-{Guid.NewGuid():N}";
        var artifactDirectoryName = BuildArtifactSnapshotDirectoryName(capturedAtUtc, snapshotId);
        var archiveFileName = CreateArtifactArchiveFileName(registration);
        var sourceDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "session-artifact-captures",
            FileNameUtil.Sanitize(sessionId),
            artifactDirectoryName);
        var sourceFilePath = Path.Combine(sourceDirectoryPath, archiveFileName);

        try
        {
            Directory.CreateDirectory(sourceDirectoryPath);
            File.Copy(registration.ArtifactPath, sourceFilePath, overwrite: true);

            var sourceFileInfo = new FileInfo(sourceFilePath);
            var entry = new SessionArtifactEntry
            {
                Name = archiveFileName,
                RootAlias = FirstNonEmpty(registration.ProviderId, "artifact"),
                RelativePath = FirstNonEmpty(registration.ArtifactId, archiveFileName),
                SnapshotRelativePath = archiveFileName,
                Kind = "file",
                SizeBytes = Math.Max(0, sizeBytes),
                FileExtension = Path.GetExtension(archiveFileName),
                MimeType = FirstNonEmpty(registration.MimeType, "application/octet-stream"),
                LastModifiedUtc = sourceFileInfo.LastWriteTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ArchiveRelativePath = archiveFileName
            };
            var snapshot = new SessionArtifactSnapshot
            {
                SnapshotId = snapshotId,
                CapturedAtUtc = capturedAtUtc,
                Source = "ansight.app.artifacts",
                RootAlias = FirstNonEmpty(registration.ProviderId, "artifact"),
                RootPath = FirstNonEmpty(registration.ProviderId, "artifact"),
                RelativePath = FirstNonEmpty(registration.ArtifactId, archiveFileName),
                Name = FirstNonEmpty(registration.Name, archiveFileName),
                Kind = FirstNonEmpty(registration.Kind, "artifact"),
                ArtifactDirectoryName = artifactDirectoryName,
                DirectoryCount = 0,
                FileCount = 1,
                ByteCount = Math.Max(0, sizeBytes),
                Truncated = false,
                Entries = [entry]
            };

            var result = runtimeState.AddSessionArtifactSnapshot(sessionId, snapshot, sourceDirectoryPath);
            if (!result.IsSuccess)
            {
                log.Info($"session_artifact_capture_failed sessionId={sessionId} transferId={registration.TransferId} snapshotId={snapshotId} message={FormatLogValue(result.Message)}");
                return null;
            }

            log.Info($"session_artifact_capture_saved sessionId={sessionId} transferId={registration.TransferId} snapshotId={snapshotId} providerId={registration.ProviderId ?? string.Empty} artifactId={registration.ArtifactId ?? string.Empty} byteCount={sizeBytes}");
            return snapshotId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Info($"session_artifact_capture_failed sessionId={sessionId} transferId={registration.TransferId} snapshotId={snapshotId} message={FormatLogValue(ex.Message)}");
            return null;
        }
        finally
        {
            TryDeleteDirectory(sourceDirectoryPath);
        }
    }

    private static string CreateArtifactArchiveFileName(BinaryToolArtifactTransferRegistration registration)
    {
        var fileName = FirstNonEmpty(
            Path.GetFileName(registration.FileName),
            Path.GetFileName(registration.ArtifactPath),
            $"{registration.TransferId}.bin");
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"{registration.TransferId}.bin"
            : sanitized;
    }

    private static string BuildArtifactSnapshotDirectoryName(DateTimeOffset capturedAtUtc, string snapshotId)
    {
        var timestamp = capturedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss.fffffff'Z'", CultureInfo.InvariantCulture);
        return $"{timestamp}-{snapshotId}";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }
    }

    private void RegisterConnection(string sessionId, LiveSessionConnection connection)
    {
        var replacedConnection = connectionRegistry.Register(sessionId, connection);

        if (replacedConnection is not null)
        {
            const string replacementReason = "The session was replaced by a newer WebSocket connection.";
            annotatedFeedbackTransfers.CancelSession(sessionId, replacementReason);
            binaryToolArtifactTransfers.CancelSession(sessionId, replacementReason);
            replacedConnection.FailPendingRequests(replacementReason);
            runtimeState.SetSessionStatus(sessionId, "WebSocket Replaced", replacementReason);
            PublishSessionCaptureEvent(
                RuntimeSessionCaptureEventKind.Stopped,
                sessionId,
                ResolveSessionStatus(sessionId, "WebSocket Replaced"),
                replacementReason);
        }

        ConnectionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool UnregisterConnection(string sessionId, LiveSessionConnection connection, string reason)
    {
        var removed = connectionRegistry.Unregister(sessionId, connection);

        if (removed)
        {
            annotatedFeedbackTransfers.CancelSession(sessionId, reason);
            binaryToolArtifactTransfers.CancelSession(sessionId, reason);
            connection.FailPendingRequests(reason);
            runtimeState.SetSessionStatus(sessionId, "WebSocket Closed", reason);
            PublishSessionCaptureEvent(
                RuntimeSessionCaptureEventKind.Stopped,
                sessionId,
                ResolveSessionStatus(sessionId, "WebSocket Closed"),
                reason);
            ConnectionsChanged?.Invoke(this, EventArgs.Empty);
            simulatorCrashReportCollector?.SearchAndAttach(sessionId, DateTimeOffset.UtcNow);
            FinalizeSessionCaptureAsync(sessionId, reason).SafeFireAndForget();
        }

        return removed;
    }

    private async Task FinalizeSessionCaptureAsync(string sessionId, string reason)
    {
        await StopCaptureSourceAsync(
            "iOS Instruments",
            sessionId,
            sdkIosInstrumentsCaptureManager is null
                ? null
                : () => sdkIosInstrumentsCaptureManager.StopAsync(sessionId)).ConfigureAwait(false);
        await StopCaptureSourceAsync(
            "native logs",
            sessionId,
            nativeSessionLogCaptureManager is null
                ? null
                : () => nativeSessionLogCaptureManager.StopAsync(sessionId, reason)).ConfigureAwait(false);
        await StopCaptureSourceAsync(
            "external screenshots",
            sessionId,
            externalSessionScreenshotCaptureManager is null
                ? null
                : () => externalSessionScreenshotCaptureManager.StopAsync(sessionId, reason)).ConfigureAwait(false);

        PublishSessionCaptureEvent(
            RuntimeSessionCaptureEventKind.Finalized,
            sessionId,
            ResolveSessionStatus(sessionId, "WebSocket Closed"),
            reason);
    }

    private static async Task StopCaptureSourceAsync(
        string sourceName,
        string sessionId,
        Func<Task>? stop)
    {
        if (stop is null)
        {
            return;
        }

        try
        {
            await stop().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Warning(
                $"session_capture_source_stop_failed sessionId={sessionId} source=\"{sourceName}\" reason=\"{exception.Message}\"");
        }
    }

    private void PublishSessionCaptureEvent(
        RuntimeSessionCaptureEventKind kind,
        string sessionId,
        string status,
        string? message)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        runtimeState.PublishRuntimeEvent(
            new RuntimeSessionCaptureEvent(
                DateTimeOffset.UtcNow,
                kind,
                sessionId,
                sessionContext.AppId,
                sessionContext.ClientName,
                string.IsNullOrWhiteSpace(status) ? sessionContext.Status : status,
                message));
    }

    private void PublishSessionTransferEvent(
        string sessionId,
        RuntimeSessionTransferKind kind,
        int itemCount,
        string message,
        DateTimeOffset? capturedAtUtc = null)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        runtimeState.PublishRuntimeEvent(
            new RuntimeSessionTransferEvent(
                DateTimeOffset.UtcNow,
                kind,
                sessionId,
                sessionContext.AppId,
                sessionContext.ClientName,
                itemCount,
                message,
                capturedAtUtc));
    }

    private void PublishAppEvent(string sessionId, SessionApplicationEvent appEvent)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        runtimeState.PublishRuntimeEvent(
            new RuntimeAppEvent(
                appEvent.CapturedAtUtc,
                appEvent.EventId,
                sessionId,
                sessionContext.AppId,
                sessionContext.ClientName,
                appEvent.Label,
                appEvent.EventType,
                appEvent.Details,
                appEvent.ChannelId));
    }

    private SessionContext ResolveSessionContext(string sessionId)
    {
        if (runtimeState.TryGetSessionContext(sessionId, out var context) && context is not null)
        {
            return new SessionContext(context.AppId, context.ClientName, context.Status);
        }

        return new SessionContext("unknown", "unknown", string.Empty);
    }

    private string ResolveSessionStatus(string sessionId, string fallbackStatus)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        return string.IsNullOrWhiteSpace(sessionContext.Status)
            ? fallbackStatus
            : sessionContext.Status;
    }

    private bool TryHandleToolBridgeResponse(string sessionId, string payload)
    {
        ToolProtocolEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ToolProtocolEnvelope>(payload, JsonUtil.Compact);
        }
        catch
        {
            return false;
        }

        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.Type)
            || (!string.Equals(envelope.Type, ToolProtocolMessageTypes.CatalogType, StringComparison.Ordinal)
                && !string.Equals(envelope.Type, ToolProtocolMessageTypes.ResultType, StringComparison.Ordinal)
                && !string.Equals(envelope.Type, AppToolProtocolContracts.BatchResultType, StringComparison.Ordinal)
                && !string.Equals(envelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)))
        {
            return false;
        }

        var encodedOriginalByteCount = ReadInteger(envelope.Payload?["originalByteCount"]);
        var encodedCompressedByteCount = ReadInteger(envelope.Payload?["compressedByteCount"]);
        if (!ToolProtocolPayloadEncoding.TryDecode(envelope.Payload, out var decodedPayload, out var decodeError))
        {
            LogUnmatchedToolBridgeResponse(sessionId, envelope, payload, decodeError);
            return true;
        }

        if (!ReferenceEquals(decodedPayload, envelope.Payload))
        {
            envelope = new ToolProtocolEnvelope
            {
                Type = envelope.Type,
                Id = envelope.Id,
                ReplyTo = envelope.ReplyTo,
                SessionId = envelope.SessionId,
                SentAt = envelope.SentAt,
                Capability = envelope.Capability,
                Payload = decodedPayload
            };
        }

        var connection = connectionRegistry.Get(sessionId);

        if (connection is null)
        {
            LogUnmatchedToolBridgeResponse(sessionId, envelope, payload, "session_bridge_closed");
            return true;
        }

        var sessionContext = ResolveSessionContext(sessionId);
        _ = binaryToolArtifactTransfers.TryRegisterAll(sessionId, sessionContext.AppId, envelope);

        if (connection.TryCompleteResponse(
                envelope,
                GetPayloadCharacterCount(payload),
                GetPayloadByteCount(payload),
                encodedOriginalByteCount,
                encodedCompressedByteCount))
        {
            return true;
        }

        binaryToolArtifactTransfers.CancelResponse(sessionId, envelope, "The tool response no longer has a waiting request.");
        LogUnmatchedToolBridgeResponse(sessionId, envelope, payload, "unmatched_reply_to");
        return true;
    }

    private void LogToolBridgeRequest(
        string sessionId,
        ToolProtocolEnvelope envelope,
        string requestBody,
        AppToolBridgeRequestContext? requestContext)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        if (IsHostOperationLogCaptureEnabled)
        {
            log.Info($"tool_bridge_request_sent source={requestContext?.Source ?? "unknown"} operation={requestContext?.Operation} correlationId={requestContext?.CorrelationId} sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} requestId={envelope.Id} requestType={envelope.Type} capability={envelope.Capability} toolId={TryReadToolId(envelope)} requestBodyChars={GetPayloadCharacterCount(requestBody)}");
        }

        trafficAuditLog.RecordToolBridgeRequest(
            sessionId,
            sessionContext.AppId,
            sessionContext.ClientName,
            envelope,
            requestBody,
            requestContext);
    }

    private void LogToolBridgeResponse(
        string sessionId,
        ToolProtocolEnvelope requestEnvelope,
        ToolProtocolEnvelope responseEnvelope,
        string requestBody,
        string responseBody,
        long durationMs,
        long appResponseDurationMs,
        ToolBridgeResponseReceipt responseReceipt,
        BinaryToolArtifactTransferMetrics? binaryTransferMetrics,
        AppToolBridgeRequestContext? requestContext)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        var isSuccess = !string.Equals(responseEnvelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal);
        var requestBodyChars = GetPayloadCharacterCount(requestBody);
        var requestBodyBytes = GetPayloadByteCount(requestBody);
        var responseBodyChars = GetPayloadCharacterCount(responseBody);
        var responseBodyBytes = GetPayloadByteCount(responseBody);
        var encodedSavedByteCount = Math.Max(
            0,
            (responseReceipt.EncodedOriginalByteCount ?? 0)
            - (responseReceipt.EncodedCompressedByteCount ?? 0));
        if (IsHostOperationLogCaptureEnabled)
        {
            log.Info($"tool_bridge_response_received source={requestContext?.Source ?? "unknown"} operation={requestContext?.Operation} correlationId={requestContext?.CorrelationId} sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} requestId={requestEnvelope.Id} replyTo={responseEnvelope.ReplyTo} requestType={requestEnvelope.Type} responseType={responseEnvelope.Type} toolId={TryReadToolId(requestEnvelope)} isSuccess={isSuccess} durationMs={durationMs} requestBodyChars={requestBodyChars} responseBodyChars={responseBodyChars}");
        }

        log.Debug($"remote_tool_invocation_metrics source={requestContext?.Source ?? "unknown"} operation={requestContext?.Operation} correlationId={requestContext?.CorrelationId} sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} requestId={requestEnvelope.Id} replyTo={responseEnvelope.ReplyTo} requestType={requestEnvelope.Type} responseType={responseEnvelope.Type} toolId={TryReadToolId(requestEnvelope)} isSuccess={isSuccess} totalDurationMs={durationMs} appResponseDurationMs={appResponseDurationMs} binaryTransferDurationMs={binaryTransferMetrics?.DurationMs ?? 0} requestBodyChars={requestBodyChars} requestBodyBytes={requestBodyBytes} decodedResponseBodyChars={responseBodyChars} decodedResponseBodyBytes={responseBodyBytes} wireResponseBodyChars={responseReceipt.WireResponseBodyChars} wireResponseBodyBytes={responseReceipt.WireResponseBodyBytes} encodedOriginalByteCount={responseReceipt.EncodedOriginalByteCount ?? 0} encodedCompressedByteCount={responseReceipt.EncodedCompressedByteCount ?? 0} encodedSavedByteCount={encodedSavedByteCount} binaryTransferId={binaryTransferMetrics?.TransferId} binaryTransferBytes={binaryTransferMetrics?.SizeBytes ?? 0}");

        trafficAuditLog.RecordToolBridgeResponse(
            sessionId,
            sessionContext.AppId,
            sessionContext.ClientName,
            requestEnvelope,
            responseEnvelope,
            requestBody,
            responseBody,
            durationMs,
            requestContext);
    }

    private void LogToolBridgeFailure(
        string sessionId,
        ToolProtocolEnvelope envelope,
        string requestBody,
        string message,
        AppToolBridgeRequestContext? requestContext,
        long durationMs)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        var requestBodyChars = GetPayloadCharacterCount(requestBody);
        var requestBodyBytes = GetPayloadByteCount(requestBody);
        if (IsHostOperationLogCaptureEnabled)
        {
            log.Info($"tool_bridge_request_failed source={requestContext?.Source ?? "unknown"} operation={requestContext?.Operation} correlationId={requestContext?.CorrelationId} sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} requestId={envelope.Id} requestType={envelope.Type} toolId={TryReadToolId(envelope)} durationMs={durationMs} message={message} requestBodyChars={requestBodyChars}");
        }

        log.Debug($"remote_tool_invocation_metrics source={requestContext?.Source ?? "unknown"} operation={requestContext?.Operation} correlationId={requestContext?.CorrelationId} sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} requestId={envelope.Id} requestType={envelope.Type} toolId={TryReadToolId(envelope)} isSuccess=false totalDurationMs={durationMs} appResponseDurationMs=0 binaryTransferDurationMs=0 requestBodyChars={requestBodyChars} requestBodyBytes={requestBodyBytes} decodedResponseBodyChars=0 decodedResponseBodyBytes=0 wireResponseBodyChars=0 wireResponseBodyBytes=0 binaryTransferBytes=0 failureMessage={FormatLogValue(message)}");

        trafficAuditLog.RecordToolBridgeFailure(
            sessionId,
            sessionContext.AppId,
            sessionContext.ClientName,
            envelope,
            requestBody,
            message,
            requestContext,
            durationMs);
    }

    private void LogUnmatchedToolBridgeResponse(
        string sessionId,
        ToolProtocolEnvelope envelope,
        string responseBody,
        string reason)
    {
        var sessionContext = ResolveSessionContext(sessionId);
        if (IsHostOperationLogCaptureEnabled)
        {
            log.Info($"tool_bridge_response_unmatched sessionId={sessionId} appId={sessionContext.AppId} clientName={sessionContext.ClientName} replyTo={envelope.ReplyTo} responseType={envelope.Type} toolId={TryReadToolId(envelope)} reason={reason} responseBodyChars={GetPayloadCharacterCount(responseBody)}");
        }

        trafficAuditLog.RecordToolBridgeUnmatchedResponse(
            sessionId,
            sessionContext.AppId,
            sessionContext.ClientName,
            envelope,
            responseBody,
            reason);
    }

    private static string SerializeEnvelope(ToolProtocolEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, PairingJson.Compact);
    }

    private static int GetPayloadCharacterCount(string? payload)
        => string.IsNullOrWhiteSpace(payload) ? 0 : payload.Length;

    private static int GetPayloadByteCount(string? payload)
        => string.IsNullOrWhiteSpace(payload) ? 0 : System.Text.Encoding.UTF8.GetByteCount(payload);

    private static int? ReadInteger(JsonNode? value)
    {
        if (value is not JsonValue jsonValue)
        {
            return null;
        }

        return jsonValue.TryGetValue<int>(out var result) ? result : null;
    }

    private static string FormatLogValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string? TryReadToolId(ToolProtocolEnvelope envelope)
    {
        if (envelope.Payload is not JsonObject payloadObject)
        {
            return null;
        }

        return payloadObject["toolId"]?.GetValue<string>();
    }

    private static bool TryAuthorizeToolBatch(
        LiveSessionConnection connection,
        ToolProtocolEnvelope envelope,
        out string? error,
        out bool requiresCatalogQuery)
    {
        requiresCatalogQuery = false;
        if (envelope.Payload is not JsonObject payload
            || payload["calls"] is not JsonArray calls
            || calls.Count is < 1 or > 32)
        {
            error = "A tool batch must contain between 1 and 32 calls.";
            return false;
        }

        foreach (var call in calls.OfType<JsonObject>())
        {
            var batchToolId = call["toolId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(batchToolId))
            {
                error = "Every batched call requires a tool ID.";
                return false;
            }

            if (!connection.CanCallTool(
                    batchToolId,
                    out error,
                    out var callRequiresCatalogQuery))
            {
                requiresCatalogQuery = callRequiresCatalogQuery;
                return false;
            }
        }

        if (calls.OfType<JsonObject>().Count() != calls.Count)
        {
            error = "Every batched call must be a JSON object.";
            return false;
        }

        error = null;
        return true;
    }

    private static long? TryReadToolResultInt64(ToolProtocolEnvelope envelope, string propertyName)
    {
        if (envelope.Payload is not JsonObject payloadObject ||
            payloadObject["result"] is not JsonObject resultObject ||
            resultObject[propertyName] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        return long.TryParse(value.ToString(), out var parsedValue)
            ? parsedValue
            : null;
    }

    private bool IsHostOperationLogCaptureEnabled => userPreferences?.CaptureHostOperationLogs ?? true;

    private static string CreateToolRequestId(string kind)
        => $"ansight.{kind}.{Guid.NewGuid():N}";

    private static async Task WaitForSessionStartupAsync(Task startedTask, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completedTask = await Task.WhenAny(startedTask, timeoutTask);
        if (completedTask != startedTask)
        {
            throw new TimeoutException("Timed out waiting for WebSocket session startup.");
        }

        await startedTask;
    }
}
