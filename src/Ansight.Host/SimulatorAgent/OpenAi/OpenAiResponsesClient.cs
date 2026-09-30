using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ansight.Host.SimulatorAgent.OpenAi;

internal sealed class OpenAiResponsesClient : IOpenAiClient
{
    private static readonly Uri ResponsesEndpoint = new("https://api.openai.com/v1/responses");
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public OpenAiResponsesClient()
        : this(new HttpClient(), ownsHttpClient: true)
    {
    }

    internal OpenAiResponsesClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.ownsHttpClient = ownsHttpClient;
    }

    public async Task<OpenAiTurn> CreateResponseAsync(
        OpenAiRequest request,
        CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        ValidateRequest(request);
        var apiKey = await request.ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var payload = BuildPayload(request, request.Input);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ResponsesEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var requestPrepared = timer.ElapsedMilliseconds;
        using var response = request.Transport is null
            ? await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await request.Transport.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var headersReceived = timer.ElapsedMilliseconds;
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var bodyReceived = timer.ElapsedMilliseconds;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException((request.Transport?.DescribeFailure((int)response.StatusCode, responseContent)
                ?? BuildFailureMessage((int)response.StatusCode, responseContent)).Replace(apiKey, "[redacted]", StringComparison.Ordinal));
        }

        JsonObject responseJson;
        try
        {
            responseJson = JsonNode.Parse(responseContent) as JsonObject
                ?? throw new InvalidOperationException("OpenAI returned an empty response object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("OpenAI returned malformed JSON.", exception);
        }

        var turn = ParseResponse(responseJson);
        return turn with
        {
            Transport = new SimulatorAgentTransportDiagnostics([
                new SimulatorAgentTransportAttempt("http", null, request.Input.Count,
                    Encoding.UTF8.GetByteCount(payload.ToJsonString()), timer.ElapsedMilliseconds)
                {
                    StartedUtc = startedUtc,
                    RequestPreparedMilliseconds = requestPrepared,
                    FirstResponseMilliseconds = headersReceived,
                    ResponseCompletedMilliseconds = bodyReceived,
                    ParsingDurationMilliseconds = timer.ElapsedMilliseconds - bodyReceived
                }
            ], 0)
        };
    }

    internal static void ValidateRequest(OpenAiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Instructions);
        ArgumentNullException.ThrowIfNull(request.Input);
        ArgumentNullException.ThrowIfNull(request.Tools);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReasoningEffort);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptCacheKey);
        if (request.MaximumOutputTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaximumOutputTokens));
        }
        if (request.CompactThresholdTokens is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.CompactThresholdTokens));
        }
    }

    internal static JsonObject BuildPayload(
        OpenAiRequest request,
        JsonArray input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new JsonObject
        {
            ["model"] = request.Model.Trim(),
            ["instructions"] = request.Instructions,
            ["input"] = input.DeepClone(),
            ["tools"] = request.Tools.DeepClone(),
            ["parallel_tool_calls"] = false,
            ["store"] = false,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["reasoning"] = new JsonObject
            {
                ["effort"] = request.ReasoningEffort.Trim()
            },
            ["prompt_cache_key"] = request.PromptCacheKey.Trim(),
            ["max_output_tokens"] = request.MaximumOutputTokens
        };
    }

    internal static JsonObject BuildWebSocketPayload(
        OpenAiRequest request,
        JsonArray input,
        bool includeInstructions)
    {
        var payload = BuildPayload(request, input);
        if (SupportsPromptCacheBreakpoints(request.Model))
        {
            payload["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "implicit",
                ["ttl"] = "30m"
            };
            if (request.UsePromptCacheBreakpoint)
            {
                payload.Remove("instructions");
                if (includeInstructions)
                {
                    ((JsonArray)payload["input"]!).Insert(0, new JsonObject
                    {
                        ["role"] = "developer",
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = request.Instructions,
                            ["prompt_cache_breakpoint"] = new JsonObject
                            {
                                ["mode"] = "explicit"
                            }
                        })
                    });
                }
            }
        }

        if (request.CompactThresholdTokens is { } compactThresholdTokens)
        {
            payload["context_management"] = new JsonArray(new JsonObject
            {
                ["type"] = "compaction",
                ["compact_threshold"] = compactThresholdTokens
            });
        }

        if (request.CompletionOnly)
        {
            payload["tool_choice"] = new JsonObject
            {
                ["type"] = "function",
                ["name"] = "complete_instruction"
            };
        }

        return payload;
    }

    internal static bool SupportsPromptCacheBreakpoints(string model)
        => !string.IsNullOrWhiteSpace(model)
           && Regex.IsMatch(
               model.Trim(),
               @"\Agpt-(?:5\.(?:[6-9]|[1-9][0-9]+)|6(?:\.(?:0|[1-9][0-9]*))?)(?:-[a-z0-9]+)*\z",
               RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static OpenAiTurn ParseResponse(JsonObject responseJson)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        var output = responseJson["output"] as JsonArray ?? [];
        var calls = new List<OpenAiFunctionCall>();
        var textParts = new List<string>();
        foreach (var item in output.OfType<JsonObject>())
        {
            var type = ReadString(item, "type");
            if (string.Equals(type, "function_call", StringComparison.Ordinal))
            {
                var callId = ReadString(item, "call_id");
                var name = ReadString(item, "name");
                if (callId is null || name is null)
                {
                    continue;
                }

                calls.Add(new OpenAiFunctionCall(
                    callId,
                    name,
                    ParseArguments(ReadString(item, "arguments"))));
                continue;
            }

            if (!string.Equals(type, "message", StringComparison.Ordinal)
                || item["content"] is not JsonArray content)
            {
                continue;
            }

            foreach (var contentItem in content.OfType<JsonObject>())
            {
                var text = ReadString(contentItem, "text");
                if (text is not null)
                {
                    textParts.Add(text);
                }
            }
        }

        var usage = responseJson["usage"] as JsonObject;
        var inputTokenDetails = usage?["input_tokens_details"] as JsonObject;
        var outputTokenDetails = usage?["output_tokens_details"] as JsonObject;
        var inputTokens = ReadInteger(usage, "input_tokens");
        var outputTokens = ReadInteger(usage, "output_tokens");
        var totalTokens = ReadInteger(usage, "total_tokens");
        return new OpenAiTurn(
            ReadString(responseJson, "id"),
            ReadString(responseJson, "model"),
            output,
            calls,
            string.Join(Environment.NewLine, textParts),
            new SimulatorAgentTokenUsage(
                inputTokens,
                outputTokens,
                totalTokens > 0 ? totalTokens : inputTokens + outputTokens,
                ReadInteger(inputTokenDetails, "cached_tokens"),
                ReadInteger(inputTokenDetails, "cache_write_tokens"),
                ReadInteger(outputTokenDetails, "reasoning_tokens")))
        {
            ResponseServiceTier = ReadString(responseJson, "service_tier")
        };
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private static JsonObject ParseArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(value) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject
            {
                ["argumentParseError"] = "The function arguments were not a JSON object."
            };
        }
    }

    internal static string BuildFailureMessage(
        int statusCode,
        string responseContent)
    {
        string? message = null;
        try
        {
            var error = JsonNode.Parse(responseContent)?["error"];
            if (error is JsonValue errorValue
                && errorValue.TryGetValue<string>(out var errorText))
            {
                message = errorText;
            }
            else if (error is JsonObject errorObject)
            {
                message = ReadString(errorObject, "message");
            }
        }
        catch (JsonException)
        {
            // Fall back to a status-only error so arbitrary response content is not surfaced.
        }

        var summary = statusCode switch
        {
            401 => "OpenAI rejected the configured API key. Check your local API key and retry.",
            402 => "OpenAI rejected the request because the configured account has no available credit.",
            403 => "OpenAI denied this API key access to the requested model or operation.",
            _ => $"OpenAI request failed with HTTP {statusCode}."
        };

        return string.IsNullOrWhiteSpace(message)
            ? summary
            : $"{summary} Server response: {message.Trim()}";
    }

    internal static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue property
           && property.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static int ReadInteger(JsonObject? value, string propertyName)
        => value?[propertyName] is JsonValue property
           && property.TryGetValue<int>(out var number)
            ? number
            : 0;
}
