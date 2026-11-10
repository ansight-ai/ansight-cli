using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Integration;

public sealed class LocalOpenAiExplorerTests
{
    [Fact(Timeout = 60000)]
    public async Task OpenAiKeyCanBeConfiguredWithoutRegisteringRunnerAndIsNeverReturned()
    {
        using var environment = new TestEnvironment();
        await using var runtime = environment.CreateRuntime();
        await runtime.StartAsync();
        var explorer = await runtime.SessionReplays.StartExplorerAsync(new SessionExplorerStartRequest());
        Assert.True(explorer.IsSuccess, explorer.Message);
        using var client = new HttpClient { BaseAddress = Assert.IsType<Uri>(explorer.ExplorerUrl) };
        const string key = "sk-test-only-not-a-real-key";

        var save = await client.PostAsJsonAsync("api/runner/openai", new { apiKey = key });
        Assert.True(save.IsSuccessStatusCode);
        Assert.DoesNotContain(key, await save.Content.ReadAsStringAsync());

        var statusText = await client.GetStringAsync("api/runner/openai");
        Assert.DoesNotContain(key, statusText);
        Assert.True(JsonNode.Parse(statusText)!["isConfigured"]!.GetValue<bool>());

        var preparation = await runtime.WorkspaceTests.RunGateway!.PrepareAsync(
            new WorkspaceTestRunPreparationRequest(null, "/workspace", "test", "Test", "com.example.test", "test-model", 1, 1));
        Assert.True(preparation.IsSuccess, preparation.Message);
        Assert.Equal(key, await preparation.ModelTransport!.ResolveAccessKeyAsync(CancellationToken.None));
        Assert.Null(preparation.TrackingRunId);

        var invalid = await client.PostAsJsonAsync("api/runner/openai", new { apiKey = "invalid key" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True(runtime.OpenAiCredentials.IsConfigured);

        var remove = await client.PostAsJsonAsync("api/runner/openai", new { remove = true });
        Assert.True(remove.IsSuccessStatusCode);
        Assert.False(runtime.OpenAiCredentials.IsConfigured);
        var removed = JsonNode.Parse(await client.GetStringAsync("api/runner/openai"))!;
        Assert.False(removed["isConfigured"]!.GetValue<bool>());
    }

}
