using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed partial class RuntimeCoordinatorIntegrationTests
{
    [Theory(Timeout = 30000)]
    [InlineData("application/x-ndjson")]
    [InlineData("application/json, application/x-ndjson; q=0.9")]
    public async Task LocalSummaryEndpoint_StreamsLoadingBeforeSessionLookup(string accept)
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(explorer.ExplorerUrl!, "api/sessions/missing-summary-session/annotation-summary"))
        {
            Content = new StringContent("""{"reasoning":"deep","startUtc":"2026-10-08T01:41:55Z","endUtc":"2026-10-08T01:42:05Z"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd(accept);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var loading = JsonNode.Parse((await reader.ReadLineAsync())!)!;
        Assert.Equal("loading", loading["progress"]?["stage"]?.GetValue<string>());
        var error = JsonNode.Parse((await reader.ReadLineAsync())!)!;
        Assert.Equal("error", error["status"]?.GetValue<string>());
        Assert.Equal("Session not found.", error["message"]?.GetValue<string>());
        Assert.Null(await reader.ReadLineAsync());
    }
}
