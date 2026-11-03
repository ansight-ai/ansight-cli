using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class OpenAiResponsesWebSocketSessionTests
{
    [Fact]
    public async Task WarmupAndGenerationAreRecordedIndependentlyOfTheRunAudit()
    {
        var connection = new FakeWebSocketConnection(CompletedEvent("resp_warmup"), CompletedEvent("resp_generation"));
        var transport = new RecordingModelTransport();
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true) with { Transport = transport };

        await session.WarmupAsync(request, CancellationToken.None);
        Assert.Equal("resp_warmup", Assert.Single(transport.Usage).ResponseId);

        await session.CreateResponseAsync(request, CancellationToken.None);
        Assert.Equal(new[] { "resp_warmup", "resp_generation" }, transport.Usage.Select(usage => usage.ResponseId));
    }

    [Theory]
    [InlineData("response.failed")]
    [InlineData("response.incomplete")]
    public async Task TerminalFailureUsageIsRecordedBeforeTheErrorIsRaised(string eventType)
    {
        var terminal = JsonNode.Parse(CompletedEvent("resp_terminal"))!.AsObject();
        terminal["type"] = eventType;
        var transport = new RecordingModelTransport();
        var connection = new FakeWebSocketConnection(terminal.ToJsonString());
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true) with { Transport = transport };

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CreateResponseAsync(request, CancellationToken.None));

        Assert.Equal("resp_terminal", Assert.Single(transport.Usage).ResponseId);
        Assert.Single(connection.SentMessages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistenceFailureDoesNotTriggerWarmupFallbackOrAnotherPaidRequest(bool warmup)
    {
        var connection = new FakeWebSocketConnection(CompletedEvent("resp_durable"));
        var transport = new RecordingModelTransport { RecordingFailure = new IOException("Disk full") };
        await using var session = new OpenAiResponsesWebSocketSession(new FakeWebSocketConnectionFactory(connection));
        var request = CreateRequest(new JsonArray("initial"), new JsonArray("initial"), true) with { Transport = transport };

        await Assert.ThrowsAsync<ModelUsageRecordingException>(async () =>
        {
            if (warmup)
            {
                await session.WarmupAsync(request, CancellationToken.None);
            }
            else
            {
                await session.CreateResponseAsync(request, CancellationToken.None);
            }
        });

        Assert.Single(connection.SentMessages);
    }
}
