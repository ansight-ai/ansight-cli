using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class OpenAiResponsesClientTests
{

    [Fact]
    public async Task CreateResponseAsync_ParsesResponseIdentityAndDetailedTokenUsage()
    {
        const string responseJson = """
            {
              "id": "resp_audit_123",
              "model": "gpt-5.6-terra-2026-07-01",
              "service_tier": "default",
              "output": [
                {
                  "type": "function_call",
                  "call_id": "call_123",
                  "name": "ansight_tap_ui",
                  "arguments": "{\"automationId\":\"continue-button\"}"
                }
              ],
              "usage": {
                "input_tokens": 1200,
                "input_tokens_details": {
                  "cached_tokens": 800,
                  "cache_write_tokens": 200
                },
                "output_tokens": 90,
                "output_tokens_details": {
                  "reasoning_tokens": 64
                },
                "total_tokens": 1290
              }
            }
            """;
        var responseHandler = new StaticResponseHandler(responseJson);
        using var httpClient = new HttpClient(responseHandler);
        using var client = new OpenAiResponsesClient(httpClient);

        var response = await client.CreateResponseAsync(
            new OpenAiRequest(
                "sk-local-test",
                "gpt-5.6-terra",
                "Test instructions",
                new JsonArray(),
                new JsonArray(),
                "low",
                "ansight-simulator-agent-v3",
                1_200),
            CancellationToken.None);

        var timing = Assert.Single(response.Transport!.Attempts);
        Assert.Equal("http", timing.Mode);
        Assert.NotNull(timing.StartedUtc);
        Assert.Null(timing.RequestSentMilliseconds);
        Assert.True(timing.FirstResponseMilliseconds >= timing.RequestPreparedMilliseconds);
        Assert.True(timing.ResponseCompletedMilliseconds >= timing.FirstResponseMilliseconds);
        Assert.NotNull(timing.ParsingDurationMilliseconds);
        Assert.Equal("resp_audit_123", response.ResponseId);
        Assert.Equal("gpt-5.6-terra-2026-07-01", response.ResponseModel);
        Assert.Equal("default", response.ResponseServiceTier);
        Assert.Equal(1200, response.Tokens.InputTokens);
        Assert.Equal(90, response.Tokens.OutputTokens);
        Assert.Equal(1290, response.Tokens.TotalTokens);
        Assert.Equal(800, response.Tokens.CachedInputTokens);
        Assert.Equal(200, response.Tokens.CacheWriteInputTokens);
        Assert.Equal(64, response.Tokens.ReasoningOutputTokens);
        var call = Assert.Single(response.FunctionCalls);
        Assert.Equal("ansight_tap_ui", call.Name);
        var requestPayload = Assert.IsType<JsonObject>(JsonNode.Parse(responseHandler.RequestBody));
        Assert.Equal("low", requestPayload["reasoning"]?["effort"]?.GetValue<string>());
        Assert.Equal("ansight-simulator-agent-v3", requestPayload["prompt_cache_key"]?.GetValue<string>());
        Assert.Equal(1_200, requestPayload["max_output_tokens"]?.GetValue<int>());
        Assert.False(requestPayload["store"]?.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", requestPayload["include"]?[0]?.GetValue<string>());
        Assert.Equal("https://api.openai.com/v1/responses", responseHandler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", responseHandler.AuthorizationScheme);
        Assert.Equal("sk-local-test", responseHandler.AuthorizationParameter);
        Assert.Null(responseHandler.ApiKeyHeader);
    }

    [Fact]
    public void BuildWebSocketPayload_PrependsStableInstructionsOnceWithoutChangingRequestHistory()
    {
        var request = CreateRequest() with
        {
            UsePromptCacheBreakpoint = true,
            CompactThresholdTokens = 32_000
        };
        var originalInput = request.Input.ToJsonString();
        var initialPayload = OpenAiResponsesClient.BuildWebSocketPayload(
            request, request.Input, includeInstructions: true);
        var replayPayload = OpenAiResponsesClient.BuildWebSocketPayload(
            request, request.Input, includeInstructions: true);

        Assert.False(initialPayload.ContainsKey("instructions"));
        var initialInput = Assert.IsType<JsonArray>(initialPayload["input"]);
        Assert.Equal(request.Input.Count + 1, initialInput.Count);
        Assert.Equal("developer", initialInput[0]?["role"]?.GetValue<string>());
        var prefixContent = Assert.Single(Assert.IsType<JsonArray>(initialInput[0]?["content"]));
        Assert.Equal("input_text", prefixContent?["type"]?.GetValue<string>());
        Assert.Equal(request.Instructions, prefixContent?["text"]?.GetValue<string>());
        Assert.Equal("explicit", prefixContent?["prompt_cache_breakpoint"]?["mode"]?.GetValue<string>());
        Assert.Equal("implicit", initialPayload["prompt_cache_options"]?["mode"]?.GetValue<string>());
        Assert.Equal("30m", initialPayload["prompt_cache_options"]?["ttl"]?.GetValue<string>());
        Assert.Equal("compaction", initialPayload["context_management"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal(32_000, initialPayload["context_management"]?[0]?["compact_threshold"]?.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(request.Tools, initialPayload["tools"]));
        Assert.True(JsonNode.DeepEquals(initialPayload, replayPayload));
        Assert.True(JsonNode.DeepEquals(request.Input[0], initialInput[1]));
        Assert.Equal(originalInput, request.Input.ToJsonString());
    }

    [Fact]
    public void BuildWebSocketPayload_ContinuationSendsOnlyNewInputAndPreservesToolSchemas()
    {
        var request = CreateRequest() with
        {
            UsePromptCacheBreakpoint = true,
            CompactThresholdTokens = 32_000,
            CompletionOnly = true
        };
        var incrementalInput = new JsonArray(new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = "call_123",
            ["output"] = "The requested screen is visible."
        });

        var payload = OpenAiResponsesClient.BuildWebSocketPayload(
            request, incrementalInput, includeInstructions: false);

        Assert.False(payload.ContainsKey("instructions"));
        Assert.True(JsonNode.DeepEquals(incrementalInput, payload["input"]));
        Assert.True(JsonNode.DeepEquals(request.Tools, payload["tools"]));
        Assert.Equal(2, Assert.IsType<JsonArray>(payload["tools"]).Count);
        Assert.Equal("function", payload["tool_choice"]?["type"]?.GetValue<string>());
        Assert.Equal("complete_instruction", payload["tool_choice"]?["name"]?.GetValue<string>());
        Assert.Equal("low", payload["reasoning"]?["effort"]?.GetValue<string>());
        Assert.False(payload["store"]?.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", payload["include"]?[0]?.GetValue<string>());
        Assert.Equal(32_000, payload["context_management"]?[0]?["compact_threshold"]?.GetValue<int>());
    }

    [Fact]
    public void BuildWebSocketPayload_WithoutExplicitBreakpointKeepsTopLevelInstructions()
    {
        var request = CreateRequest();

        var payload = OpenAiResponsesClient.BuildWebSocketPayload(
            request, request.Input, includeInstructions: false);

        Assert.Equal(request.Instructions, payload["instructions"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(request.Input, payload["input"]));
        Assert.Equal("implicit", payload["prompt_cache_options"]?["mode"]?.GetValue<string>());
        Assert.False(payload.ContainsKey("context_management"));
        Assert.False(payload.ContainsKey("tool_choice"));
    }

    [Theory]
    [InlineData("gpt-5.5", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-4.1", false)]
    [InlineData("custom-model", false)]
    public void BuildWebSocketPayload_UnsupportedModelKeepsCompatibleInstructions(
        string model,
        bool includeInstructions)
    {
        var request = CreateRequest() with
        {
            Model = model,
            UsePromptCacheBreakpoint = true
        };

        var payload = OpenAiResponsesClient.BuildWebSocketPayload(
            request, request.Input, includeInstructions);

        Assert.True(JsonNode.DeepEquals(OpenAiResponsesClient.BuildPayload(request, request.Input), payload));
        Assert.Equal(request.Instructions, payload["instructions"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(request.Input, payload["input"]));
        Assert.False(payload.ContainsKey("prompt_cache_options"));
    }

    [Theory]
    [InlineData("gpt-5.6", true)]
    [InlineData("gpt-5.6-terra-2026-07-01", true)]
    [InlineData("gpt-5.7-sol", true)]
    [InlineData("gpt-5.10", true)]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-6.1", true)]
    [InlineData("  gpt-5.6-terra  ", true)]
    [InlineData("gpt-5.5", false)]
    [InlineData("gpt-5.5-2026-05-01", false)]
    [InlineData("gpt-5.6preview", false)]
    [InlineData("gpt-6other", false)]
    [InlineData("gpt-5.6-", false)]
    [InlineData("gpt-5.06", false)]
    [InlineData("gpt-7", false)]
    [InlineData("custom-model-gpt-5.6", false)]
    [InlineData("", false)]
    public void SupportsPromptCacheBreakpoints_RecognizesOnlySupportedModelFamilies(
        string model,
        bool expected)
    {
        Assert.Equal(expected, OpenAiResponsesClient.SupportsPromptCacheBreakpoints(model));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRequest_RejectsNonpositiveCompactionThreshold(int compactThresholdTokens)
    {
        var request = CreateRequest() with { CompactThresholdTokens = compactThresholdTokens };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            OpenAiResponsesClient.ValidateRequest(request));

        Assert.Equal(nameof(request.CompactThresholdTokens), exception.ParamName);
    }

    [Fact]
    public void ParseResponse_PreservesCompactionAndEncryptedReasoningForReplay()
    {
        var output = new JsonArray(
            new JsonObject
            {
                ["type"] = "reasoning",
                ["id"] = "rs_123",
                ["encrypted_content"] = "encrypted-reasoning"
            },
            new JsonObject
            {
                ["type"] = "compaction",
                ["id"] = "cmp_123",
                ["encrypted_content"] = "encrypted-compaction"
            });

        var response = OpenAiResponsesClient.ParseResponse(new JsonObject
        {
            ["id"] = "resp_compacted",
            ["output"] = output.DeepClone()
        });

        Assert.True(JsonNode.DeepEquals(output, response.Output));
        Assert.Empty(response.FunctionCalls);
        Assert.Empty(response.AssistantText);
    }

    private static OpenAiRequest CreateRequest()
        => new(
            "sk-local-test",
            "gpt-5.6-terra",
            "Follow the test instructions and report the result.",
            new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Open the settings screen."
            }),
            new JsonArray(
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "ansight_tap_ui",
                    ["parameters"] = new JsonObject { ["type"] = "object" }
                },
                new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = "complete_instruction",
                    ["parameters"] = new JsonObject { ["type"] = "object" }
                }),
            "low",
            "ansight-simulator-agent-test",
            1_200);

    private sealed class StaticResponseHandler(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? ApiKeyHeader { get; private set; }

        public string? TestRunIdHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            ApiKeyHeader = request.Headers.TryGetValues("apikey", out var apiKeyValues)
                ? apiKeyValues.Single()
                : null;
            TestRunIdHeader = request.Headers.TryGetValues(
                "X-Ansight-Test-Run-Id",
                out var testRunIdValues)
                ? testRunIdValues.Single()
                : null;
            RequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()
                          ?? string.Empty;
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
