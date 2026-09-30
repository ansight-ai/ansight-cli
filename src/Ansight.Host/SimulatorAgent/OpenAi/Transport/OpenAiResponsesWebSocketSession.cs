using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.OpenAi.Transport;

internal sealed class OpenAiResponsesWebSocketSession : IOpenAiSession
{
    private static readonly Uri ResponsesEndpoint = new("wss://api.openai.com/v1/responses");
    private const string StreamId = "main";
    private readonly IOpenAiWebSocketConnectionFactory connectionFactory;
    private IOpenAiWebSocketConnection? connection;
    private string? connectionApiKey;
    private string? previousResponseId;
    private WarmupConfiguration? warmupConfiguration;
    private readonly List<SimulatorAgentTransportAttempt> warmupAttempts = [];
    private readonly List<SimulatorAgentModelPassUsage> warmupResponses = [];
    private SimulatorAgentTokenUsage warmupTokens = SimulatorAgentTokenUsage.Empty;
    private int warmupCompactionCount;
    private bool disposed;

    public SimulatorAgentTransportDiagnostics? LastTransportDiagnostics { get; private set; }

    public OpenAiResponsesWebSocketSession()
        : this(new OpenAiWebSocketConnectionFactory())
    {
    }

    internal OpenAiResponsesWebSocketSession(IOpenAiWebSocketConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory
                                 ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public async Task WarmupAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        previousResponseId = null;
        warmupConfiguration = null;
        warmupAttempts.Clear();
        warmupResponses.Clear();
        warmupTokens = SimulatorAgentTokenUsage.Empty;
        warmupCompactionCount = 0;
        try
        {
            var response = await SendResponseAsync(
                request,
                [],
                includeInstructions: true,
                "warmup",
                replayReason: null,
                warmupAttempts,
                generate: false,
                cancellationToken).ConfigureAwait(false);
            previousResponseId = response.ResponseId;
            warmupTokens = response.Tokens;
            warmupCompactionCount = CountCompactions(response);
            if (previousResponseId is not null)
            {
                warmupConfiguration = GetWarmupConfiguration(request);
                warmupResponses.Add(new SimulatorAgentModelPassUsage(
                    previousResponseId,
                    response.ResponseModel ?? request.Model,
                    response.ResponseServiceTier,
                    DateTimeOffset.UtcNow,
                    response.Tokens));
            }
        }
        catch (Exception exception) when (exception is OpenAiWebSocketTransportException
                                          or OpenAiPreviousResponseNotFoundException
                                          or InvalidOperationException)
        {
            // Warming the prefix is optional. A generation can recover with the complete input.
            await ResetConnectionAsync().ConfigureAwait(false);
            previousResponseId = null;
        }
        catch (OperationCanceledException)
        {
            await ResetConnectionAsync().ConfigureAwait(false);
            previousResponseId = null;
            throw;
        }
        finally
        {
            LastTransportDiagnostics = CreateDiagnostics(warmupAttempts, warmupCompactionCount);
        }
    }

    public async Task<OpenAiTurn> CreateResponseAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var attempts = new List<SimulatorAgentTransportAttempt>(warmupAttempts);
        warmupAttempts.Clear();
        var hadPreviousResponse = previousResponseId is not null;
        var hasWarmup = warmupConfiguration is not null;
        var useWarmup = hasWarmup
                        && previousResponseId is not null
                        && warmupConfiguration == GetWarmupConfiguration(request);
        warmupConfiguration = null;
        var reconnect = previousResponseId is not null && connection?.State != WebSocketState.Open;
        if (reconnect || (request.StartNewConversation || hasWarmup) && !useWarmup)
        {
            previousResponseId = null;
        }

        var mustReplayFullInput = previousResponseId is null
                                  || !useWarmup && (request.IncrementalInput is null
                                                   || request.StartNewConversation);
        var replayReason = mustReplayFullInput
            ? reconnect
                ? "reconnect"
                : request.ReplayReason ?? (hasWarmup || hadPreviousResponse
                    ? "instruction-boundary"
                    : "initial")
            : null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var completed = false;
            try
            {
                var response = await SendResponseAsync(
                    request,
                    mustReplayFullInput || useWarmup ? request.Input : request.IncrementalInput!,
                    includeInstructions: mustReplayFullInput,
                    mustReplayFullInput ? "full" : "incremental",
                    replayReason,
                    attempts,
                    generate: true,
                    cancellationToken).ConfigureAwait(false);
                previousResponseId = response.ResponseId;
                LastTransportDiagnostics = CreateDiagnostics(attempts, warmupCompactionCount + CountCompactions(response));
                warmupTokens = SimulatorAgentTokenUsage.Empty;
                warmupCompactionCount = 0;
                warmupResponses.Clear();
                completed = true;
                return response with
                {
                    Transport = LastTransportDiagnostics
                };
            }
            catch (OpenAiPreviousResponseNotFoundException) when (attempt == 0)
            {
                previousResponseId = null;
                mustReplayFullInput = true;
                useWarmup = false;
                replayReason = "previous-response-not-found";
            }
            catch (OpenAiPreviousResponseNotFoundException)
            {
                throw new InvalidOperationException(
                    "OpenAI could not recover the previous WebSocket response after a full context replay.");
            }
            catch (OpenAiWebSocketTransportException) when (attempt == 0)
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                previousResponseId = null;
                mustReplayFullInput = true;
                useWarmup = false;
                replayReason = "reconnect";
            }
            catch (OperationCanceledException)
            {
                await ResetConnectionAsync().ConfigureAwait(false);
                previousResponseId = null;
                throw;
            }
            finally
            {
                // Failed requests still need an auditable record when the runner falls back or stops.
                if (!completed)
                {
                    LastTransportDiagnostics = CreateDiagnostics(attempts, warmupCompactionCount);
                }
            }
        }

        throw new OpenAiWebSocketTransportException(
            "The OpenAI WebSocket request could not be completed after reconnecting.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await ResetConnectionAsync().ConfigureAwait(false);
    }

    private async Task<OpenAiTurn> SendResponseAsync(
        OpenAiRequest request,
        JsonArray input,
        bool includeInstructions,
        string mode,
        string? replayReason,
        List<SimulatorAgentTransportAttempt> attempts,
        bool generate,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        long connectionDurationMilliseconds = 0;
        var connectionSucceeded = false;
        long? requestPrepared = null, requestSent = null, firstResponse = null, responseCompleted = null, parsingDuration = null;
        var requestBytes = 0;
        var inputItemCount = 0;
        string? error = null;
        try
        {
            bool credentialChanged;
            try
            {
                credentialChanged = await EnsureConnectedAsync(request, cancellationToken).ConfigureAwait(false);
                connectionSucceeded = true;
            }
            finally { connectionDurationMilliseconds = stopwatch.ElapsedMilliseconds; }
            if (credentialChanged)
            {
                previousResponseId = null;
                if (generate)
                {
                    input = request.Input;
                    includeInstructions = true;
                    mode = "full";
                    replayReason = "credential-renewal";
                }
            }
            var payload = OpenAiResponsesClient.BuildWebSocketPayload(request, input, includeInstructions);
            payload["type"] = "response.create";
            payload["stream_id"] = StreamId;
            if (!generate)
            {
                payload["generate"] = false;
            }
            if (!includeInstructions && previousResponseId is not null)
            {
                payload["previous_response_id"] = previousResponseId;
            }

            var messageJson = payload.ToJsonString();
            inputItemCount = payload["input"]?.AsArray().Count ?? 0;
            requestBytes = Encoding.UTF8.GetByteCount(messageJson);
            requestPrepared = stopwatch.ElapsedMilliseconds;
            await connection!.SendTextAsync(messageJson, cancellationToken)
                .ConfigureAwait(false);
            requestSent = stopwatch.ElapsedMilliseconds;
            while (true)
            {
                var message = await connection.ReceiveTextAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    throw new OpenAiWebSocketTransportException(
                        "The OpenAI WebSocket connection closed before the response completed.");
                }

                firstResponse ??= stopwatch.ElapsedMilliseconds;
                var parseStarted = stopwatch.ElapsedMilliseconds;
                JsonObject responseEvent;
                try
                {
                    responseEvent = JsonNode.Parse(message) as JsonObject
                                    ?? throw new JsonException("The event was not a JSON object.");
                }
                catch (JsonException exception)
                {
                    throw new OpenAiWebSocketTransportException(
                        "OpenAI returned a malformed WebSocket event.",
                        exception);
                }

                var eventType = OpenAiResponsesClient.ReadString(responseEvent, "type");
                if (string.Equals(eventType, "response.completed", StringComparison.Ordinal))
                {
                    var response = responseEvent["response"] as JsonObject
                                   ?? throw new OpenAiWebSocketTransportException(
                                       "OpenAI returned a completed WebSocket event without a response object.");
                    responseCompleted = parseStarted;
                    var turn = OpenAiResponsesClient.ParseResponse(response);
                    parsingDuration = stopwatch.ElapsedMilliseconds - parseStarted;
                    return turn;
                }

                if (string.Equals(eventType, "error", StringComparison.Ordinal))
                {
                    ThrowResponseError(responseEvent, request.Transport);
                }

                if (eventType is "response.failed" or "response.incomplete")
                {
                    ThrowTerminalResponseError(responseEvent, eventType);
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            error = exception.Message;
            throw;
        }
        catch (OpenAiPreviousResponseNotFoundException exception)
        {
            error = exception.Message;
            throw;
        }
        catch (OpenAiWebSocketTransportException exception)
        {
            error = exception.Message;
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException
                                          or HttpRequestException
                                          or IOException)
        {
            error = exception.Message;
            throw new OpenAiWebSocketTransportException(
                "The OpenAI WebSocket transport failed.",
                exception);
        }
        catch (Exception exception)
        {
            error = connectionApiKey is null ? exception.Message : exception.Message.Replace(connectionApiKey, "[redacted]", StringComparison.Ordinal);
            if (error != exception.Message)
            {
                throw new InvalidOperationException(error);
            }
            throw;
        }
        finally
        {
            attempts.Add(new SimulatorAgentTransportAttempt(
                mode,
                replayReason,
                inputItemCount,
                requestBytes,
                stopwatch.ElapsedMilliseconds)
            {
                RequestPreparedMilliseconds = requestPrepared,
                RequestSentMilliseconds = requestSent,
                FirstResponseMilliseconds = firstResponse,
                ResponseCompletedMilliseconds = responseCompleted,
                ParsingDurationMilliseconds = parsingDuration,
                ConnectionSucceeded = connectionSucceeded,
                StartedUtc = startedUtc,
                ConnectionDurationMilliseconds = connectionDurationMilliseconds,
                Error = error
            });
        }
    }

    private SimulatorAgentTransportDiagnostics CreateDiagnostics(
        List<SimulatorAgentTransportAttempt> attempts,
        int compactionCount)
        => new(attempts.ToArray(), compactionCount)
        {
            WarmupTokens = warmupTokens,
            WarmupResponses = warmupResponses.ToArray()
        };

    private static int CountCompactions(OpenAiTurn response)
        => response.Output.OfType<JsonObject>().Count(item =>
            string.Equals(OpenAiResponsesClient.ReadString(item, "type"), "compaction", StringComparison.Ordinal));

    private void ValidateRequest(OpenAiRequest request)
    {
        ThrowIfDisposed();
        OpenAiResponsesClient.ValidateRequest(request);
        if (request.Transport?.SupportsWebSockets == false)
        {
            throw new ArgumentException(
                "OpenAI WebSocket mode is only available for direct local execution.",
                nameof(request));
        }
    }

    private static WarmupConfiguration GetWarmupConfiguration(OpenAiRequest request)
        => new(request.ApiKey.Trim(), OpenAiResponsesClient.BuildWebSocketPayload(request, [], includeInstructions: true).ToJsonString());

    private sealed record WarmupConfiguration(string ApiKey, string Payload);

    private async Task<bool> EnsureConnectedAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = await request.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var credentialChanged = connectionApiKey is not null && !string.Equals(connectionApiKey, apiKey, StringComparison.Ordinal);
        if (connection?.State == WebSocketState.Open && !credentialChanged)
        {
            return false;
        }

        await ResetConnectionAsync().ConfigureAwait(false);
        connection = connectionFactory.Create();
        connectionApiKey = apiKey;
        try
        {
            await connection.ConnectAsync(ResponsesEndpoint, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception.ToString().Contains(apiKey, StringComparison.Ordinal))
        {
            var message = exception.Message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
            // Some handshake failures echo Authorization. Do not retain a secret in an inner exception.
            if (exception is OperationCanceledException) throw new OperationCanceledException(message, cancellationToken);
            if (exception is InvalidOperationException) throw new InvalidOperationException(message);
            throw new OpenAiWebSocketTransportException(message);
        }
        return credentialChanged;
    }

    private async ValueTask ResetConnectionAsync()
    {
        if (connection is null)
        {
            return;
        }

        var currentConnection = connection;
        connection = null;
        connectionApiKey = null;
        await currentConnection.DisposeAsync().ConfigureAwait(false);
    }

    private static void ThrowResponseError(JsonObject responseEvent, IModelExecutionTransport? transport)
    {
        var error = responseEvent["error"] as JsonObject;
        var code = error is null ? null : OpenAiResponsesClient.ReadString(error, "code");
        if (string.Equals(code, "previous_response_not_found", StringComparison.Ordinal))
        {
            throw new OpenAiPreviousResponseNotFoundException();
        }
        if (string.Equals(code, "websocket_connection_limit_reached", StringComparison.Ordinal))
        {
            throw new OpenAiWebSocketTransportException("The OpenAI WebSocket connection reached its lifetime limit.");
        }

        var status = responseEvent["status"]?.GetValue<int>() ?? 0;
        if (status > 0 && error is not null)
        {
            var errorPayload = new JsonObject
            {
                ["error"] = error.DeepClone()
            };
            throw new InvalidOperationException((transport?.DescribeFailure(status, errorPayload.ToJsonString())
                ?? OpenAiResponsesClient.BuildFailureMessage(status, errorPayload.ToJsonString())));
        }

        var message = error is null ? null : OpenAiResponsesClient.ReadString(error, "message");
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? "OpenAI rejected the WebSocket response request."
            : $"OpenAI rejected the WebSocket response request: {message}");
    }

    private static void ThrowTerminalResponseError(JsonObject responseEvent, string eventType)
    {
        var response = responseEvent["response"] as JsonObject;
        var error = response?["error"] as JsonObject;
        var incompleteDetails = response?["incomplete_details"] as JsonObject;
        var responseId = response is null ? null : OpenAiResponsesClient.ReadString(response, "id");
        var reason = incompleteDetails is null ? null : OpenAiResponsesClient.ReadString(incompleteDetails, "reason");
        var errorCode = error is null ? null : OpenAiResponsesClient.ReadString(error, "code");
        var message = error is null ? null : OpenAiResponsesClient.ReadString(error, "message");
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(responseId))
        {
            details.Add($"response_id={responseId}");
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            details.Add($"reason={reason}");
        }
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            details.Add($"error_code={errorCode}");
        }
        var diagnosticDetails = details.Count == 0 ? string.Empty : $" ({string.Join(", ", details)})";
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"OpenAI returned terminal WebSocket event '{eventType}'{diagnosticDetails}."
            : $"OpenAI returned terminal WebSocket event '{eventType}'{diagnosticDetails}: {message}");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed class OpenAiPreviousResponseNotFoundException : Exception
    {
    }
}
