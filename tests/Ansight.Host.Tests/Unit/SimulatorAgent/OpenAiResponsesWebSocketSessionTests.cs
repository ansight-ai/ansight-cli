using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class OpenAiResponsesWebSocketSessionTests
{
    [Fact]
    public async Task CreateResponseAsync_ContinuesWithPreviousResponseAndIncrementalInput()
    {
        var connection = new FakeWebSocketConnection(
            CompletedEvent("resp_1"),
            CompletedEvent("resp_2"));
        var connectionFactory = new FakeWebSocketConnectionFactory(connection);
        await using var session = new OpenAiResponsesWebSocketSession(connectionFactory);

        var first = await session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), startNewConversation: true),
            CancellationToken.None);
        var second = await session.CreateResponseAsync(
            CreateRequest(
                new JsonArray("initial", "tool-result"),
                new JsonArray("tool-result"),
                startNewConversation: false),
            CancellationToken.None);

        Assert.Equal("resp_1", first.ResponseId);
        Assert.Equal("resp_2", second.ResponseId);
        Assert.Equal(new Uri("wss://api.openai.com/v1/responses"), connection.Endpoint);
        Assert.Equal("sk-local-test", connection.ApiKey);
        Assert.Equal(2, connection.SentMessages.Count);
        var firstPayload = ParseObject(connection.SentMessages[0]);
        Assert.Equal("response.create", firstPayload["type"]?.GetValue<string>());
        Assert.Equal("main", firstPayload["stream_id"]?.GetValue<string>());
        Assert.Null(firstPayload["previous_response_id"]);
        Assert.Equal("initial", firstPayload["input"]?[0]?.GetValue<string>());
        var secondPayload = ParseObject(connection.SentMessages[1]);
        Assert.Equal("resp_1", secondPayload["previous_response_id"]?.GetValue<string>());
        Assert.Single(secondPayload["input"]?.AsArray() ?? []);
        Assert.Equal("tool-result", secondPayload["input"]?[0]?.GetValue<string>());
        Assert.Equal("Test instructions", secondPayload["instructions"]?.GetValue<string>());
        Assert.Equal("full", Assert.Single(first.Transport!.Attempts).Mode);
        var timing = Assert.Single(first.Transport.Attempts);
        Assert.NotNull(timing.StartedUtc);
        Assert.True(timing.ConnectionSucceeded);
        Assert.True(timing.RequestSentMilliseconds >= timing.RequestPreparedMilliseconds);
        Assert.True(timing.FirstResponseMilliseconds >= timing.RequestSentMilliseconds);
        Assert.True(timing.ResponseCompletedMilliseconds >= timing.FirstResponseMilliseconds);
        Assert.NotNull(timing.ParsingDurationMilliseconds);
        Assert.InRange(timing.ConnectionDurationMilliseconds!.Value, 0, timing.DurationMilliseconds);
        Assert.Equal("initial", Assert.Single(first.Transport.Attempts).ReplayReason);
        Assert.Equal("incremental", Assert.Single(second.Transport!.Attempts).Mode);
    }

    [Fact]
    public async Task CreateResponseAsync_ReplaysFullInputWhenPreviousResponseIsUnavailable()
    {
        var connection = new FakeWebSocketConnection(
            CompletedEvent("resp_1"),
            ErrorEvent("previous_response_not_found", "Previous response unavailable."),
            CompletedEvent("resp_2"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(connection));

        await session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), startNewConversation: true),
            CancellationToken.None);
        var response = await session.CreateResponseAsync(
            CreateRequest(
                new JsonArray("initial", "tool-result"),
                new JsonArray("tool-result"),
                startNewConversation: false),
            CancellationToken.None);

        Assert.Equal("resp_2", response.ResponseId);
        Assert.Equal(3, connection.SentMessages.Count);
        var failedContinuation = ParseObject(connection.SentMessages[1]);
        Assert.Equal("resp_1", failedContinuation["previous_response_id"]?.GetValue<string>());
        var replay = ParseObject(connection.SentMessages[2]);
        Assert.Null(replay["previous_response_id"]);
        Assert.Equal(2, replay["input"]?.AsArray().Count);
        Assert.Collection(response.Transport!.Attempts,
            attempt =>
            {
                Assert.Equal("incremental", attempt.Mode);
                Assert.NotNull(attempt.Error);
            },
            attempt =>
            {
                Assert.Equal("full", attempt.Mode);
                Assert.Equal("previous-response-not-found", attempt.ReplayReason);
            });
    }

    [Fact]
    public async Task CreateResponseAsync_ReconnectsAndReplaysFullInputAfterTransportClose()
    {
        var closedConnection = new FakeWebSocketConnection((string?)null);
        var recoveredConnection = new FakeWebSocketConnection(CompletedEvent("resp_recovered"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(closedConnection, recoveredConnection));

        var response = await session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), startNewConversation: true),
            CancellationToken.None);

        Assert.Equal("resp_recovered", response.ResponseId);
        Assert.True(closedConnection.IsDisposed);
        Assert.Single(closedConnection.SentMessages);
        Assert.Single(recoveredConnection.SentMessages);
        var replay = ParseObject(recoveredConnection.SentMessages[0]);
        Assert.Null(replay["previous_response_id"]);
        Assert.Equal("initial", replay["input"]?[0]?.GetValue<string>());
        Assert.Equal("reconnect", response.Transport!.Attempts[1].ReplayReason);
        Assert.NotNull(response.Transport.Attempts[0].Error);
    }

    [Fact]
    public async Task CreateResponseAsync_RecordsExactRequestBytesForFullAndIncrementalTurns()
    {
        var initialInput = new string('x', 8_000) + " café";
        var connection = new FakeWebSocketConnection(
            CompletedEvent("resp_1"),
            CompletedEvent("resp_2"),
            CompletedEvent("resp_3"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var first = await session.CreateResponseAsync(
            CreateRequest(new JsonArray(initialInput), new JsonArray(initialInput), true), CancellationToken.None);
        var second = await session.CreateResponseAsync(
            CreateRequest(new JsonArray(initialInput, "result-1"), new JsonArray("result-1"), false), CancellationToken.None);
        var third = await session.CreateResponseAsync(
            CreateRequest(new JsonArray(initialInput, "result-1", "result-2"), new JsonArray("result-2"), false), CancellationToken.None);

        var responses = new[] { first, second, third };
        for (var index = 0; index < responses.Length; index++)
        {
            var attempt = Assert.Single(responses[index].Transport!.Attempts);
            Assert.Equal(Encoding.UTF8.GetByteCount(connection.SentMessages[index]), attempt.RequestBytes);
            Assert.Equal(1, attempt.InputItemCount);
            Assert.True(attempt.DurationMilliseconds >= 0);
            Assert.Null(attempt.Error);
        }
        Assert.True(first.Transport!.Attempts[0].RequestBytes > second.Transport!.Attempts[0].RequestBytes * 2);
        Assert.Equal("resp_2", ParseObject(connection.SentMessages[2])["previous_response_id"]?.GetValue<string>());
        Assert.Equal("incremental", third.Transport!.Attempts[0].Mode);
        Assert.Same(third.Transport, session.LastTransportDiagnostics);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("previous-response-not-found")]
    [InlineData("reconnect")]
    public async Task CreateResponseAsync_PreservesLoadedToolsInConversationWhileKeepingCoreSchemasStable(string recovery)
    {
        var loaderCall = new JsonObject
        {
            ["type"] = "function_call", ["call_id"] = "load_1",
            ["name"] = "ansight_load_tools", ["arguments"] = "{\"capability\":\"gestures\"}"
        };
        var gestureCall = new JsonObject
        {
            ["type"] = "function_call", ["call_id"] = "swipe_1",
            ["name"] = "ansight_swipe_ui", ["arguments"] = "{\"direction\":\"up\"}"
        };
        var receivedEvents = new List<string?>
        {
            CompletedEvent("resp_1", new JsonArray(loaderCall.DeepClone())),
            CompletedEvent("resp_2", new JsonArray(gestureCall.DeepClone()))
        };
        if (recovery == "previous-response-not-found")
        {
            receivedEvents.Add(ErrorEvent("previous_response_not_found", "Previous response unavailable."));
        }
        receivedEvents.Add(recovery == "reconnect" ? null : CompletedEvent("resp_3"));
        var connection = new FakeWebSocketConnection(receivedEvents.ToArray());
        var reconnected = new FakeWebSocketConnection(CompletedEvent("resp_3"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(connection, reconnected));
        var coreTools = new JsonArray(new JsonObject
        {
            ["type"] = "function", ["name"] = "ansight_load_tools",
            ["parameters"] = new JsonObject { ["type"] = "object" }
        });
        var initialInput = new JsonArray(new JsonObject
        {
            ["role"] = "user", ["content"] = "Scroll the current page down."
        });
        var request = CreateRequest(initialInput, initialInput, true) with
        {
            Model = "gpt-5.6-luna", Tools = coreTools, UsePromptCacheBreakpoint = true
        };
        await session.CreateResponseAsync(request, CancellationToken.None);
        var loadedTools = new JsonObject
        {
            ["type"] = "additional_tools", ["role"] = "developer",
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function", ["name"] = "ansight_swipe_ui",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["direction"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("direction"), ["additionalProperties"] = false
                }
            })
        };
        var loadedInput = new JsonArray(
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "load_1", ["output"] = "Gestures loaded." },
            loadedTools,
            new JsonObject { ["role"] = "developer", ["content"] = "Swipe direction describes finger travel." });
        var fullInput = (JsonArray)initialInput.DeepClone();
        fullInput.Add(loaderCall.DeepClone());
        foreach (var item in loadedInput)
        {
            fullInput.Add(item!.DeepClone());
        }
        var loadedTurn = await session.CreateResponseAsync(request with
        {
            Input = fullInput, IncrementalInput = loadedInput, StartNewConversation = false
        }, CancellationToken.None);
        var parsedGesture = Assert.Single(loadedTurn.FunctionCalls);
        Assert.Equal("ansight_swipe_ui", parsedGesture.Name);
        Assert.Equal("up", parsedGesture.Arguments["direction"]?.GetValue<string>());
        var gestureResult = new JsonObject
        {
            ["type"] = "function_call_output", ["call_id"] = "swipe_1", ["output"] = "Swipe delivered."
        };
        fullInput.Add(gestureCall.DeepClone());
        fullInput.Add(gestureResult.DeepClone());
        var expectedHistory = fullInput.ToJsonString();
        await session.CreateResponseAsync(request with
        {
            Input = fullInput, IncrementalInput = new JsonArray(gestureResult), StartNewConversation = false
        }, CancellationToken.None);

        var sent = connection.SentMessages.Concat(reconnected.SentMessages).Select(ParseObject).ToArray();
        Assert.Equal(recovery == "none" ? 3 : 4, sent.Length);
        Assert.All(sent, payload => Assert.True(JsonNode.DeepEquals(coreTools, payload["tools"])));
        Assert.True(JsonNode.DeepEquals(loadedInput, sent[1]["input"]));
        Assert.Equal("resp_1", sent[1]["previous_response_id"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(gestureResult, Assert.Single(sent[2]["input"]!.AsArray())));
        Assert.Equal("resp_2", sent[2]["previous_response_id"]?.GetValue<string>());
        if (recovery != "none")
        {
            var replay = sent[3];
            Assert.Null(replay["previous_response_id"]);
            var replayInput = replay["input"]!.AsArray();
            Assert.Equal(fullInput.Count + 1, replayInput.Count);
            for (var index = 0; index < fullInput.Count; index++)
            {
                Assert.True(JsonNode.DeepEquals(fullInput[index], replayInput[index + 1]));
            }
            Assert.Single(replayInput.OfType<JsonObject>(), item => item["type"]?.GetValue<string>() == "additional_tools");
        }
        Assert.Equal(expectedHistory, fullInput.ToJsonString());
        Assert.Single(coreTools);
    }

    [Fact]
    public async Task WarmupAsync_CachesOnlyInstructionsThenAppendsCompleteInitialInputWithoutDuplicatingPrefix()
    {
        var connection = new FakeWebSocketConnection(
            CompletedEvent("resp_warmup"), CompletedEvent("resp_1"), CompletedEvent("resp_2"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial", "observation"), new JsonArray("observation"), true) with
        {
            UsePromptCacheBreakpoint = true,
            CompactThresholdTokens = 32_000
        };

        await session.WarmupAsync(request, CancellationToken.None);
        var first = await session.CreateResponseAsync(request, CancellationToken.None);
        var second = await session.CreateResponseAsync(request with
        {
            Input = new JsonArray("initial", "observation", "result"),
            IncrementalInput = new JsonArray("result"),
            StartNewConversation = false
        }, CancellationToken.None);

        var warmup = ParseObject(connection.SentMessages[0]);
        Assert.False(warmup["generate"]!.GetValue<bool>());
        var developerMessage = Assert.Single(warmup["input"]!.AsArray())!.AsObject();
        Assert.Equal("developer", developerMessage["role"]?.GetValue<string>());
        Assert.Equal("Test instructions", developerMessage["content"]?[0]?["text"]?.GetValue<string>());
        Assert.Null(warmup["instructions"]);
        var generated = ParseObject(connection.SentMessages[1]);
        Assert.Null(generated["generate"]);
        Assert.Equal("resp_warmup", generated["previous_response_id"]?.GetValue<string>());
        Assert.Equal(2, generated["input"]!.AsArray().Count);
        Assert.Equal("initial", generated["input"]?[0]?.GetValue<string>());
        Assert.Equal("observation", generated["input"]?[1]?.GetValue<string>());
        Assert.Null(generated["instructions"]);
        Assert.Collection(first.Transport!.Attempts,
            attempt => Assert.Equal("warmup", attempt.Mode),
            attempt => Assert.Equal("incremental", attempt.Mode));
        Assert.Equal(2, first.Transport.WarmupTokens.TotalTokens);
        Assert.Equal("resp_warmup", Assert.Single(first.Transport.WarmupResponses).ResponseId);
        Assert.Single(second.Transport!.Attempts);
        Assert.Equal(SimulatorAgentTokenUsage.Empty, second.Transport.WarmupTokens);
        Assert.Empty(second.Transport.WarmupResponses);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("instructions")]
    [InlineData("tools")]
    [InlineData("reasoning")]
    [InlineData("cache-key")]
    [InlineData("cache-breakpoint")]
    [InlineData("threshold")]
    [InlineData("output-limit")]
    [InlineData("completion-only")]
    public async Task WarmupAsync_ReplaysFullInputWhenRequestConfigurationChanges(string changedSetting)
    {
        var connection = new FakeWebSocketConnection(CompletedEvent("resp_warmup"), CompletedEvent("resp_generated"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("delta"), true) with
        {
            UsePromptCacheBreakpoint = true,
            CompactThresholdTokens = 32_000
        };
        await session.WarmupAsync(request, CancellationToken.None);
        var changed = changedSetting switch
        {
            "model" => request with { Model = "gpt-5.6-sol" },
            "instructions" => request with { Instructions = "Changed instructions" },
            "tools" => request with { Tools = new JsonArray(new JsonObject { ["type"] = "function", ["name"] = "inspect" }) },
            "reasoning" => request with { ReasoningEffort = "high" },
            "cache-key" => request with { PromptCacheKey = "other-key" },
            "cache-breakpoint" => request with { UsePromptCacheBreakpoint = false },
            "threshold" => request with { CompactThresholdTokens = 64_000 },
            "output-limit" => request with { MaximumOutputTokens = 2_000 },
            "completion-only" => request with { CompletionOnly = true },
            _ => throw new InvalidOperationException()
        };
        var response = await session.CreateResponseAsync(changed, CancellationToken.None);

        var payload = ParseObject(connection.SentMessages[1]);
        Assert.Null(payload["previous_response_id"]);
        Assert.Equal("initial", payload["input"]!.AsArray().Last()!.GetValue<string>());
        Assert.Equal("full", response.Transport!.Attempts[1].Mode);
        Assert.Equal("instruction-boundary", response.Transport.Attempts[1].ReplayReason);
    }

    [Fact]
    public async Task WarmupAsync_ReplaysFullInputWhenWarmedResponseIsUnavailable()
    {
        var connection = new FakeWebSocketConnection(
            CompletedEvent("resp_warmup"),
            ErrorEvent("previous_response_not_found", "Warmup response unavailable."),
            CompletedEvent("resp_recovered"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("delta"), true) with { UsePromptCacheBreakpoint = true };

        await session.WarmupAsync(request, CancellationToken.None);
        var response = await session.CreateResponseAsync(request, CancellationToken.None);

        Assert.Equal("resp_warmup", ParseObject(connection.SentMessages[1])["previous_response_id"]?.GetValue<string>());
        var replay = ParseObject(connection.SentMessages[2]);
        Assert.Null(replay["previous_response_id"]);
        Assert.Equal("developer", replay["input"]?[0]?["role"]?.GetValue<string>());
        Assert.Equal("initial", replay["input"]?[1]?.GetValue<string>());
        Assert.Equal("previous-response-not-found", response.Transport!.Attempts[2].ReplayReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarmupAsync_FallsBackToFullInputAfterTransportFailureOrWarmupRejection(bool rejected)
    {
        var failedConnection = new FakeWebSocketConnection(rejected
            ? ErrorEvent("unsupported_parameter", "Warmup unavailable.")
            : null);
        var recoveredConnection = new FakeWebSocketConnection(CompletedEvent("resp_generated"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(failedConnection, recoveredConnection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("delta"), true);

        await session.WarmupAsync(request, CancellationToken.None);
        var response = await session.CreateResponseAsync(request, CancellationToken.None);

        Assert.True(failedConnection.IsDisposed);
        var generated = ParseObject(Assert.Single(recoveredConnection.SentMessages));
        Assert.Null(generated["previous_response_id"]);
        Assert.Equal("initial", generated["input"]?[0]?.GetValue<string>());
        Assert.NotNull(response.Transport!.Attempts[0].Error);
        Assert.Equal("full", response.Transport.Attempts[1].Mode);
    }

    [Fact]
    public async Task WarmupAsync_CancellationResetsConnectionBeforeSubsequentGeneration()
    {
        var canceledConnection = new FakeWebSocketConnection
        {
            ReceiveException = new OperationCanceledException()
        };
        var recoveredConnection = new FakeWebSocketConnection(CompletedEvent("resp_generated"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(canceledConnection, recoveredConnection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("delta"), true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WarmupAsync(request, CancellationToken.None));
        var response = await session.CreateResponseAsync(request, CancellationToken.None);

        Assert.True(canceledConnection.IsDisposed);
        Assert.Null(ParseObject(Assert.Single(recoveredConnection.SentMessages))["previous_response_id"]);
        Assert.NotNull(response.Transport!.Attempts[0].Error);
    }

    [Fact]
    public async Task CreateResponseAsync_DoesNotSuppressGenerationRejectionAndRetainsFailureDiagnostics()
    {
        var connection = new FakeWebSocketConnection(ErrorEvent("invalid_request", "Generation rejected."));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true), CancellationToken.None));

        Assert.Single(connection.SentMessages);
        Assert.Contains("Generation rejected", Assert.Single(session.LastTransportDiagnostics!.Attempts).Error);
    }

    [Fact]
    public async Task CreateResponseAsync_IncompleteResponseReportsReasonAndIdWithoutRetryingOrReturningPartialTools()
    {
        var incompleteEvent = new JsonObject
        {
            ["type"] = "response.incomplete",
            ["response"] = new JsonObject
            {
                ["id"] = "resp_incomplete",
                ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
                ["output"] = new JsonArray(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = "partial_call",
                    ["name"] = "tap",
                    ["arguments"] = "private-partial-tool-arguments"
                })
            }
        }.ToJsonString();
        var connection = new FakeWebSocketConnection(incompleteEvent, CompletedEvent("resp_unexpected_retry"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateResponseAsync(
            CreateRequest(new JsonArray("private-input"), new JsonArray("private-input"), true), CancellationToken.None));

        Assert.Contains("response.incomplete", exception.Message);
        Assert.Contains("response_id=resp_incomplete", exception.Message);
        Assert.Contains("reason=max_output_tokens", exception.Message);
        Assert.DoesNotContain("private-partial-tool-arguments", exception.Message);
        Assert.DoesNotContain("private-input", exception.Message);
        Assert.Single(connection.SentMessages);
        Assert.Equal(exception.Message, Assert.Single(session.LastTransportDiagnostics!.Attempts).Error);
    }

    [Fact]
    public async Task CreateResponseAsync_FailedResponseReportsErrorCodeMessageAndIdWithoutRetrying()
    {
        var failedEvent = new JsonObject
        {
            ["type"] = "response.failed",
            ["response"] = new JsonObject
            {
                ["id"] = "resp_failed",
                ["error"] = new JsonObject
                {
                    ["code"] = "server_error",
                    ["message"] = "The model could not complete this response."
                },
                ["output"] = new JsonArray("private-response-output")
            }
        }.ToJsonString();
        var connection = new FakeWebSocketConnection(failedEvent, CompletedEvent("resp_unexpected_retry"));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true), CancellationToken.None));

        Assert.Contains("response.failed", exception.Message);
        Assert.Contains("response_id=resp_failed", exception.Message);
        Assert.Contains("error_code=server_error", exception.Message);
        Assert.Contains("The model could not complete this response.", exception.Message);
        Assert.DoesNotContain("private-response-output", exception.Message);
        Assert.Single(connection.SentMessages);
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    public async Task CreateResponseAsync_TerminalResponseWithoutDetailsKeepsUsefulEventMessage(string eventType)
    {
        var connection = new FakeWebSocketConnection(new JsonObject { ["type"] = eventType }.ToJsonString());
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true), CancellationToken.None));

        Assert.Equal($"OpenAI returned terminal WebSocket event '{eventType}'.", exception.Message);
        Assert.Single(connection.SentMessages);
    }

    [Fact]
    public async Task CreateResponseAsync_RecordsServerCompactionWithoutDroppingOutput()
    {
        var output = new JsonArray(new JsonObject { ["type"] = "compaction", ["encrypted_content"] = "opaque" });
        var connection = new FakeWebSocketConnection(CompletedEvent("resp_compacted", output));
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));

        var response = await session.CreateResponseAsync(
            CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true), CancellationToken.None);

        Assert.Equal(1, response.Transport!.CompactionCount);
        Assert.Equal("opaque", response.Output[0]?["encrypted_content"]?.GetValue<string>());
    }

    [Fact]
    public async Task CreateResponseAsync_ReconnectsAtLifetimeLimitAndReplaysCheckpointAndPendingResultExactlyOnce()
    {
        var firstConnection = new FakeWebSocketConnection(CompletedEvent("resp_1"),
            ErrorEvent("websocket_connection_limit_reached", "Reconnect required."));
        var recoveredConnection = new FakeWebSocketConnection(CompletedEvent("resp_2"));
        await using var session = new OpenAiResponsesWebSocketSession(
            new FakeWebSocketConnectionFactory(firstConnection, recoveredConnection));
        await session.CreateResponseAsync(CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true),
            CancellationToken.None);
        var checkpoint = new JsonObject { ["type"] = "compaction", ["encrypted_content"] = "checkpoint" };
        var reasoning = new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = "reasoning", ["summary"] = new JsonArray() };
        var call = new JsonObject { ["type"] = "function_call", ["call_id"] = "call-1", ["name"] = "inspect", ["arguments"] = "{}" };
        var result = new JsonObject { ["type"] = "function_call_output", ["call_id"] = "call-1", ["output"] = "Ready" };
        var input = new JsonArray(checkpoint, reasoning, call, result);

        var response = await session.CreateResponseAsync(CreateRequest(input, new JsonArray(result.DeepClone()), false) with
        {
            UsePromptCacheBreakpoint = true,
            CompactThresholdTokens = 32_000
        }, CancellationToken.None);

        var replay = ParseObject(Assert.Single(recoveredConnection.SentMessages));
        Assert.Null(replay["previous_response_id"]);
        Assert.Equal("developer", replay["input"]?[0]?["role"]?.GetValue<string>());
        Assert.Equal(5, replay["input"]!.AsArray().Count);
        for (var index = 0; index < input.Count; index++)
        {
            Assert.True(JsonNode.DeepEquals(input[index], replay["input"]?[index + 1]));
        }
        Assert.Equal("reconnect", response.Transport!.Attempts[1].ReplayReason);
        Assert.True(firstConnection.IsDisposed);
    }

    private static OpenAiRequest CreateRequest(
        JsonArray fullInput,
        JsonArray incrementalInput,
        bool startNewConversation)
        => new(
            "sk-local-test",
            "gpt-5.6-terra",
            "Test instructions",
            fullInput,
            [],
            "medium",
            "ansight-simulator-agent-test",
            1_200)
        {
            IncrementalInput = incrementalInput,
            StartNewConversation = startNewConversation
        };

    private static string CompletedEvent(string responseId, JsonArray? output = null)
        => new JsonObject
        {
            ["type"] = "response.completed",
            ["stream_id"] = "main",
            ["response"] = new JsonObject
            {
                ["id"] = responseId,
                ["model"] = "gpt-5.6-terra",
                ["output"] = output ?? new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = 1,
                    ["output_tokens"] = 1,
                    ["total_tokens"] = 2
                }
            }
        }.ToJsonString();

    private static string ErrorEvent(string code, string message)
        => new JsonObject
        {
            ["type"] = "error",
            ["status"] = 400,
            ["stream_id"] = "main",
            ["error"] = new JsonObject
            {
                ["type"] = "invalid_request_error",
                ["code"] = code,
                ["message"] = message
            }
        }.ToJsonString();

    private static JsonObject ParseObject(string value)
        => JsonNode.Parse(value) as JsonObject
           ?? throw new InvalidOperationException("Expected a JSON object.");

    private sealed class FakeWebSocketConnectionFactory(
        params FakeWebSocketConnection[] connections) : IOpenAiWebSocketConnectionFactory
    {
        private readonly Queue<FakeWebSocketConnection> remainingConnections = new(connections);

        public IOpenAiWebSocketConnection Create()
            => remainingConnections.Dequeue();
    }

    private sealed class FakeWebSocketConnection(
        params string?[] receivedMessages) : IOpenAiWebSocketConnection
    {
        private readonly Queue<string?> remainingMessages = new(receivedMessages);

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Uri? Endpoint { get; private set; }

        public string? ApiKey { get; private set; }

        public List<string> SentMessages { get; } = [];

        public bool IsDisposed { get; private set; }

        public Exception? ReceiveException { get; init; }

        public Exception? ConnectException { get; init; }

        public Action? OnReceive { get; init; }

        public Task ConnectAsync(
            Uri endpoint,
            string apiKey,
            CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            ApiKey = apiKey;
            if (ConnectException is not null) return Task.FromException(ConnectException);
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            OnReceive?.Invoke();
            return ReceiveException is not null
                ? Task.FromException<string?>(ReceiveException)
                : Task.FromResult(remainingMessages.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }
}
