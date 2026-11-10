using System.Net;
using Ansight.Host.SimulatorAgent.OpenAi;
using Ansight.Host.SimulatorAgent.OpenAi.Transport;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class LocalOpenAiTests
{
    [Fact]
    public async Task SavedKeyUsesDirectTransportAndRemovalRestoresCloudGateway()
    {
        var storage = new InMemoryEncryptedStorage();
        var credentials = new LocalOpenAiCredentialStore(storage);
        var cloud = new TestCloudGateway();
        var gateway = new CloudWorkspaceTestRunGateway(() => cloud, credentials);
        var request = CreateRequest();

        credentials.Save(" sk-local-test-only ");
        Assert.True(new LocalOpenAiCredentialStore(storage).IsConfigured);

        var direct = await gateway.PrepareAsync(request);
        Assert.True(direct.IsSuccess);
        Assert.Null(direct.TrackingRunId);
        Assert.False(direct.UsesExternalTransport);
        Assert.Equal("test-model", direct.ReasoningConfiguration!.Model);
        Assert.Equal("high", direct.ReasoningConfiguration.ReasoningEffort);
        Assert.Equal("sk-local-test-only", await direct.ModelTransport!.ResolveAccessKeyAsync(CancellationToken.None));
        Assert.Equal(0, cloud.PreparationCount);

        credentials.Remove();
        Assert.False(credentials.IsConfigured);
        await gateway.PrepareAsync(request);
        Assert.Equal(1, cloud.PreparationCount);

        // Removing the saved key only changes future runs.
        Assert.Equal("sk-local-test-only", await direct.ModelTransport.ResolveAccessKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitRunnerCredentialsKeepTheirOwnGateway()
    {
        var credentials = new LocalOpenAiCredentialStore(new InMemoryEncryptedStorage());
        credentials.Save("sk-local-test-only");
        var cloud = new TestCloudGateway();
        var gateway = new CloudWorkspaceTestRunGateway(() => cloud, credentials);
        using var scope = RunnerAiCredentialContext.Push(new RunnerAiCredentials("caller-test-key", null));

        var result = await gateway.PrepareAsync(CreateRequest());

        Assert.Equal(1, cloud.PreparationCount);
        Assert.Null(result.ModelTransport);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-invalid key")]
    [InlineData("sk-invalid\nkey")]
    public void InvalidReplacementKeepsSavedKey(string? value)
    {
        var credentials = new LocalOpenAiCredentialStore(new InMemoryEncryptedStorage());
        credentials.Save("sk-original-test-key");

        Assert.Throws<ArgumentException>(() => credentials.Save(value));
        Assert.Equal("sk-original-test-key", credentials.Read());
    }

    [Fact]
    public async Task DirectTransportAuthenticatesOnlyOpenAiAndDoesNotEchoProviderErrors()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var transport = new LocalOpenAiTransport("sk-test-only", client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        using var response = await transport.SendAsync(request, CancellationToken.None);

        Assert.Equal("Bearer sk-test-only", handler.Authorization);
        Assert.Equal("api.openai.com", handler.Host);
        Assert.True(transport.SupportsWebSockets);
        Assert.DoesNotContain("sk-test-only", transport.DescribeFailure(401, "Invalid key: sk-test-only"));

        using var wrongHost = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/responses");
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendAsync(wrongHost, CancellationToken.None));
        Assert.Null(wrongHost.Headers.Authorization);
    }

    private static WorkspaceTestRunPreparationRequest CreateRequest()
    {
        return new WorkspaceTestRunPreparationRequest(null, "/workspace", "test", "Test", "com.example.test", "test-model", 1, 1)
        {
            Reasoning = AgentReasoningModes.Deep
        };
    }

    private sealed class TestCloudGateway : IWorkspaceTestRunGateway
    {
        public int PreparationCount { get; private set; }

        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request, CancellationToken cancellationToken = default)
        {
            PreparationCount++;
            return Task.FromResult(WorkspaceTestRunPreparation.Failure("Cloud gateway selected."));
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Direct runs must not invoke cloud metering.");
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? Host { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Host = request.RequestUri?.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
