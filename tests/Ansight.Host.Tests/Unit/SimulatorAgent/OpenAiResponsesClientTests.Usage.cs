using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed partial class OpenAiResponsesClientTests
{
    [Fact]
    public async Task ResponseUsageIsRecordedBeforeTheCallerReceivesTheResponse()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("""
            {"id":"resp_durable","model":"gpt-6-luna","service_tier":"default",
             "usage":{"input_tokens":120,"output_tokens":30,"input_tokens_details":{"cached_tokens":80}}}
            """));
        var transport = new RecordingModelTransport { Client = httpClient };
        using var client = new OpenAiResponsesClient(httpClient);

        var response = await client.CreateResponseAsync(CreateRequest() with { Transport = transport }, CancellationToken.None);

        var usage = Assert.Single(transport.Usage);
        Assert.Equal(response.ResponseId, usage.ResponseId);
        Assert.Equal("gpt-6-luna", usage.Model);
        Assert.Equal("default", usage.ServiceTier);
        Assert.Equal(150, usage.Tokens.TotalTokens);
        Assert.Equal(80, usage.Tokens.CachedInputTokens);
    }

    [Fact]
    public async Task PersistenceFailureStopsExecutionInsteadOfReturningAnUnrecordedResponse()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler("""
            {"id":"resp_durable","usage":{"input_tokens":120,"output_tokens":30}}
            """));
        var transport = new RecordingModelTransport { Client = httpClient, RecordingFailure = new IOException("Disk full") };
        using var client = new OpenAiResponsesClient(httpClient);

        await Assert.ThrowsAsync<ModelUsageRecordingException>(() =>
            client.CreateResponseAsync(CreateRequest() with { Transport = transport }, CancellationToken.None));
    }
}
