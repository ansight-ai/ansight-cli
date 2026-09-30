namespace Ansight.Host.Runtime.WebSocketSessions;

using Ansight.Pairing;
using Ansight.Pairing.Models;
using Ansight.Tools;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Host.Runtime.Operations;
using Ansight.Infrastructure.Preferences;
using AppLifecycleState = global::Ansight.AppLifecycleState;
using HostOperationResult = Ansight.Host.Runtime.Contracts.OperationResult;

internal sealed class LiveSessionConnection
{
    private readonly string sessionId;
    private readonly WebSocket socket;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<ToolBridgeResponseReceipt>> pendingResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthorizedToolDescriptor> toolsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> sentRequestIds = new(StringComparer.Ordinal);
    private bool disconnectRequested;
    private bool hasQueriedToolCatalog;
    private string? toolCatalogRevision;
    private string? disconnectReason;

    public LiveSessionConnection(
        string sessionId,
        WebSocket socket,
        PairingSessionAuthorization authorization)
    {
        this.sessionId = sessionId;
        this.socket = socket;
        Authorization = authorization;
    }

    public string SessionId => sessionId;

    public PairingSessionAuthorization Authorization { get; }

    internal AppToolCatalogCache ToolCatalogCache { get; } = new();

    internal SemaphoreSlim ToolCatalogQueryLock { get; } = new(1, 1);

    public bool IsDisconnectRequested
    {
        get
        {
            lock (gate)
            {
                return disconnectRequested;
            }
        }
    }

    public string? DisconnectReason
    {
        get
        {
            lock (gate)
            {
                return disconnectReason;
            }
        }
    }

    public void RequestDisconnect(string reason)
    {
        lock (gate)
        {
            disconnectRequested = true;
            disconnectReason = string.IsNullOrWhiteSpace(reason)
                ? "Live app connection closed."
                : reason.Trim();
        }
    }

    public void Abort()
    {
        socket.Abort();
    }

    public async Task<ToolBridgeResponseReceipt> SendRequestAsync(
        ToolProtocolEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var responseSource = new TaskCompletionSource<ToolBridgeResponseReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (!sentRequestIds.Add(envelope.Id))
            {
                throw new InvalidOperationException($"Duplicate tool request id '{envelope.Id}' was rejected.");
            }

            pendingResponses[envelope.Id] = responseSource;
        }

        try
        {
            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    throw new InvalidOperationException("The session WebSocket is not open.");
                }

                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, PairingJson.Compact);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
            return await responseSource.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for app session '{sessionId}' to answer {envelope.Type}.", ex);
        }
        finally
        {
            RemovePendingResponse(envelope.Id, responseSource);
        }
    }

    public bool TryCompleteResponse(
        ToolProtocolEnvelope envelope,
        int wireResponseBodyChars,
        int wireResponseBodyBytes,
        int? encodedOriginalByteCount,
        int? encodedCompressedByteCount)
    {
        UpdateToolCatalog(envelope);

        if (string.IsNullOrWhiteSpace(envelope.ReplyTo))
        {
            return false;
        }

        TaskCompletionSource<ToolBridgeResponseReceipt>? responseSource;
        lock (gate)
        {
            if (!pendingResponses.TryGetValue(envelope.ReplyTo, out responseSource))
            {
                return false;
            }

            pendingResponses.Remove(envelope.ReplyTo);
        }

        return responseSource.TrySetResult(new ToolBridgeResponseReceipt(
            envelope,
            wireResponseBodyChars,
            wireResponseBodyBytes,
            encodedOriginalByteCount,
            encodedCompressedByteCount));
    }

    public bool CanQueryTools(out string? reason)
    {
        reason = null;
        return true;
    }

    public string? GetToolCatalogRevision()
    {
        lock (gate)
        {
            return toolCatalogRevision;
        }
    }

    public void ApplyToolCatalog(JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        UpdateToolCatalogPayload(payload);
    }

    public void InvalidateToolCatalog()
    {
        lock (gate)
        {
            hasQueriedToolCatalog = false;
            toolCatalogRevision = null;
            toolsById.Clear();
        }

        ToolCatalogCache.Clear();
    }

    public bool CanCallTool(
        string toolId,
        out string? reason,
        out bool requiresCatalogQuery)
    {
        lock (gate)
        {
            if (!toolsById.TryGetValue(toolId, out var descriptor))
            {
                requiresCatalogQuery = true;
                reason = hasQueriedToolCatalog
                    ? $"Tool '{toolId}' was not exposed by the last authenticated tool catalog."
                    : $"Query the authenticated tool catalog before invoking tool '{toolId}'.";
                return false;
            }

            if (!descriptor.IsExecutable)
            {
                requiresCatalogQuery = false;
                reason = descriptor.DenialReason ?? $"Tool '{descriptor.ToolId}' is not executable in the current session.";
                return false;
            }
        }

        requiresCatalogQuery = false;
        reason = null;
        return true;
    }

    public void FailPendingRequests(string reason)
    {
        TaskCompletionSource<ToolBridgeResponseReceipt>[] pendingResponseSources;
        lock (gate)
        {
            pendingResponseSources = pendingResponses.Values.ToArray();
            pendingResponses.Clear();
        }

        var exception = new IOException(reason);
        foreach (var responseSource in pendingResponseSources)
        {
            responseSource.TrySetException(exception);
        }
    }

    private void RemovePendingResponse(
        string requestId,
        TaskCompletionSource<ToolBridgeResponseReceipt> expectedSource)
    {
        lock (gate)
        {
            if (pendingResponses.TryGetValue(requestId, out var existing)
                && ReferenceEquals(existing, expectedSource))
            {
                pendingResponses.Remove(requestId);
            }
        }
    }

    private void UpdateToolCatalog(ToolProtocolEnvelope envelope)
    {
        if (!string.Equals(envelope.Type, ToolProtocolMessageTypes.CatalogType, StringComparison.Ordinal)
            || envelope.Payload is not JsonObject payload)
        {
            return;
        }

        UpdateToolCatalogPayload(payload);
    }

    private void UpdateToolCatalogPayload(JsonObject payload)
    {
        lock (gate)
        {
            hasQueriedToolCatalog = true;
            var revision = ReadString(payload["revision"]);
            if (!string.IsNullOrWhiteSpace(revision))
            {
                toolCatalogRevision = revision;
            }

            if (ReadBoolean(payload["unchanged"], fallback: false))
            {
                return;
            }

            if (payload["tools"] is not JsonArray tools)
            {
                return;
            }

            var isDefinitionProjection = string.Equals(
                ReadString(payload["detail"]),
                "definitions",
                StringComparison.Ordinal);
            if (!isDefinitionProjection)
            {
                toolsById.Clear();
            }
            foreach (var tool in tools.OfType<JsonObject>())
            {
                var id = ReadString(tool["id"]);
                var policy = ReadString(tool["policy"]);
                if (string.IsNullOrWhiteSpace(id) || !PairingToolPolicy.TryParse(policy, out _))
                {
                    continue;
                }

                var runtime = tool["runtime"] as JsonObject;
                var runtimeAvailable = ReadBoolean(runtime?["available"], fallback: true);
                var appExecutable = ReadBoolean(tool["executable"], fallback: true);
                var evaluation = EvaluateToolExecution(
                    policy!,
                    runtimeAvailable,
                    appExecutable,
                    runtime);

                if (evaluation.IsExecutable)
                {
                    tool.Remove("denial");
                }
                else
                {
                    tool["denial"] = new JsonObject
                    {
                        ["code"] = evaluation.DenialCode,
                        ["reason"] = evaluation.DenialReason
                    };
                }
                tool["executable"] = evaluation.IsExecutable;
                toolsById[id] = new AuthorizedToolDescriptor(
                    id,
                    PairingToolPolicy.Normalize(policy),
                    evaluation.IsExecutable,
                    evaluation.DenialCode,
                    evaluation.DenialReason);
            }
        }
    }

    private ToolExecutionEvaluation EvaluateToolExecution(
        string policy,
        bool runtimeAvailable,
        bool appExecutable,
        JsonObject? runtime)
    {
        if (!Authorization.AllowsPolicy(policy))
        {
            var reason = $"Tool policy '{PairingToolPolicy.Normalize(policy)}' exceeds the authenticated client grant '{Authorization.MaxToolPolicy}'.";
            return ToolExecutionEvaluation.Denied("tool_policy_not_granted", reason);
        }

        if (!runtimeAvailable)
        {
            return ToolExecutionEvaluation.Denied(
                ReadString(runtime?["code"])
                ?? ReadString(runtime?["reasonCode"])
                ?? "tool_unavailable",
                ReadString(runtime?["reason"]) ?? "The tool is not available in the current app state.");
        }

        if (!appExecutable)
        {
            return ToolExecutionEvaluation.Denied(
                "tool_not_executable",
                "The app reported that this tool is not executable.");
        }

        return ToolExecutionEvaluation.Allowed;
    }

    private static bool ReadBoolean(JsonNode? node, bool fallback)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : fallback;
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private sealed record ToolExecutionEvaluation(
        bool IsExecutable,
        string? DenialCode,
        string? DenialReason)
    {
        public static ToolExecutionEvaluation Allowed { get; } = new(true, null, null);

        public static ToolExecutionEvaluation Denied(
            string code,
            string reason)
            => new(false, code, reason);
    }
}
