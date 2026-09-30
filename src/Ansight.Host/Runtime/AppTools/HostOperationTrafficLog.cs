using System.ComponentModel.Composition;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Infrastructure;
using Ansight.Infrastructure.Logging;
using Ansight.Infrastructure.Preferences;
using Ansight.Tools;

namespace Ansight.Host.Runtime.AppTools;

[Export(typeof(IHostOperationTrafficLog))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class HostOperationTrafficLog : IHostOperationTrafficLog
{
    private readonly Lock gate = new();
    private readonly IUserPreferences? userPreferences;
    private readonly string directoryPath;
    private LogFileWriter? writer;
    private string? currentFilePath;

    [ImportingConstructor]
    public HostOperationTrafficLog(
        IApplicationPaths applicationPaths,
        [Import(AllowDefault = true)] IUserPreferences? userPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        directoryPath = Path.Combine(applicationPaths.ApplicationLogsPath, "host-operation-traffic");
        this.userPreferences = userPreferences;

        if (this.userPreferences is not null)
        {
            this.userPreferences.Changed += HandleUserPreferencesChanged;
        }
    }

    public bool IsCaptureEnabled => userPreferences?.CaptureFullHostOperationTrafficToDisk ?? false;

    public string DirectoryPath => directoryPath;

    public string? CurrentFilePath
    {
        get
        {
            lock (gate)
            {
                return currentFilePath;
            }
        }
    }

    public void RecordToolBridgeRequest(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string requestBody,
        AppToolBridgeRequestContext? requestContext)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Append(
            new JsonObject
            {
                ["kind"] = "tool_bridge_request_sent",
                ["occurredAtUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = sessionId,
                ["appId"] = appId,
                ["clientName"] = clientName,
                ["source"] = requestContext?.Source,
                ["operation"] = requestContext?.Operation,
                ["correlationId"] = requestContext?.CorrelationId,
                ["requestId"] = envelope.Id,
                ["requestType"] = envelope.Type,
                ["capability"] = envelope.Capability,
                ["toolId"] = TryReadToolId(envelope),
                ["requestBodyChars"] = GetPayloadCharacterCount(requestBody),
                ["requestBodyBytes"] = GetPayloadByteCount(requestBody),
                ["requestBody"] = requestBody
            });
    }

    public void RecordToolBridgeResponse(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope requestEnvelope,
        ToolProtocolEnvelope responseEnvelope,
        string requestBody,
        string responseBody,
        long durationMs,
        AppToolBridgeRequestContext? requestContext)
    {
        ArgumentNullException.ThrowIfNull(requestEnvelope);
        ArgumentNullException.ThrowIfNull(responseEnvelope);

        Append(
            new JsonObject
            {
                ["kind"] = "tool_bridge_response_received",
                ["occurredAtUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = sessionId,
                ["appId"] = appId,
                ["clientName"] = clientName,
                ["source"] = requestContext?.Source,
                ["operation"] = requestContext?.Operation,
                ["correlationId"] = requestContext?.CorrelationId,
                ["requestId"] = requestEnvelope.Id,
                ["replyTo"] = responseEnvelope.ReplyTo,
                ["requestType"] = requestEnvelope.Type,
                ["responseType"] = responseEnvelope.Type,
                ["toolId"] = TryReadToolId(requestEnvelope),
                ["isSuccess"] = !string.Equals(responseEnvelope.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal),
                ["durationMs"] = durationMs,
                ["requestBodyChars"] = GetPayloadCharacterCount(requestBody),
                ["requestBodyBytes"] = GetPayloadByteCount(requestBody),
                ["responseBodyChars"] = GetPayloadCharacterCount(responseBody),
                ["responseBodyBytes"] = GetPayloadByteCount(responseBody),
                ["requestBody"] = requestBody,
                ["responseBody"] = responseBody
            });
    }

    public void RecordToolBridgeFailure(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string requestBody,
        string message,
        AppToolBridgeRequestContext? requestContext,
        long durationMs)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Append(
            new JsonObject
            {
                ["kind"] = "tool_bridge_request_failed",
                ["occurredAtUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = sessionId,
                ["appId"] = appId,
                ["clientName"] = clientName,
                ["source"] = requestContext?.Source,
                ["operation"] = requestContext?.Operation,
                ["correlationId"] = requestContext?.CorrelationId,
                ["requestId"] = envelope.Id,
                ["requestType"] = envelope.Type,
                ["toolId"] = TryReadToolId(envelope),
                ["durationMs"] = durationMs,
                ["message"] = message,
                ["requestBodyChars"] = GetPayloadCharacterCount(requestBody),
                ["requestBodyBytes"] = GetPayloadByteCount(requestBody),
                ["requestBody"] = requestBody
            });
    }

    public void RecordToolBridgeUnmatchedResponse(
        string sessionId,
        string appId,
        string clientName,
        ToolProtocolEnvelope envelope,
        string responseBody,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        Append(
            new JsonObject
            {
                ["kind"] = "tool_bridge_response_unmatched",
                ["occurredAtUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = sessionId,
                ["appId"] = appId,
                ["clientName"] = clientName,
                ["replyTo"] = envelope.ReplyTo,
                ["responseType"] = envelope.Type,
                ["toolId"] = TryReadToolId(envelope),
                ["reason"] = reason,
                ["responseBodyChars"] = GetPayloadCharacterCount(responseBody),
                ["responseBodyBytes"] = GetPayloadByteCount(responseBody),
                ["responseBody"] = responseBody
            });
    }

    public void Dispose()
    {
        if (userPreferences is not null)
        {
            userPreferences.Changed -= HandleUserPreferencesChanged;
        }

        lock (gate)
        {
            writer?.Dispose();
            writer = null;
            currentFilePath = null;
        }
    }

    private void Append(JsonObject payload)
    {
        if (!IsCaptureEnabled)
        {
            return;
        }

        var line = JsonSerializer.Serialize(payload, JsonUtil.Compact);

        lock (gate)
        {
            if (!IsCaptureEnabled)
            {
                return;
            }

            EnsureWriterLocked();
            writer!.WriteToFile(line, appendLineEnding: true);
        }
    }

    private void HandleUserPreferencesChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (!string.Equals(e.Key, PreferenceKeys.CaptureFullHostOperationTrafficToDisk, StringComparison.Ordinal)
            || IsCaptureEnabled)
        {
            return;
        }

        lock (gate)
        {
            writer?.Dispose();
            writer = null;
            currentFilePath = null;
        }
    }

    private void EnsureWriterLocked()
    {
        if (writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(directoryPath);
        LogRetentionPolicy.DeleteExpiredLogs(directoryPath);
        currentFilePath = Path.Combine(
            directoryPath,
            $"host-operation-traffic-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        writer = new LogFileWriter(currentFilePath);
    }

    private static string? TryReadToolId(ToolProtocolEnvelope envelope)
    {
        if (envelope.Payload is not JsonObject payloadObject)
        {
            return null;
        }

        return payloadObject["toolId"]?.GetValue<string>();
    }

    private static int GetPayloadCharacterCount(string? payload)
        => string.IsNullOrWhiteSpace(payload) ? 0 : payload.Length;

    private static int GetPayloadByteCount(string? payload)
        => string.IsNullOrWhiteSpace(payload) ? 0 : System.Text.Encoding.UTF8.GetByteCount(payload);
}
