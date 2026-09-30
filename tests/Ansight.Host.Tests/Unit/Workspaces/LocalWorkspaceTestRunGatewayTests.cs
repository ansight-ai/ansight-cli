using Ansight.Host.Workspaces.Cloud;
using Ansight.Host.Workspaces.Execution;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class LocalWorkspaceTestRunGatewayTests
{
    private static readonly WorkspaceTestRunPreparationRequest request = new(
        null, "/workspace", "test", "Test", "com.example.app", "test-model", 1, 1);

    [Fact]
    public async Task LocalCredentialPreparesAnUnmeteredRunWithoutCallingCloud()
    {
        var cloud = new CloudGateway();
        var gateway = new LocalWorkspaceTestRunGateway(() => throw new InvalidOperationException("Local preparation must not initialize an extension."), () => " developer-key ", () => false);
        var result = await gateway.PrepareAsync(request);
        Assert.True(result.IsSuccess);
        Assert.Equal("developer-key", result.ApiKey);
        Assert.Equal("test-model", result.ReasoningConfiguration!.Model);
        Assert.False(result.UsesExternalTransport);
        Assert.Null(result.TrackingRunId);
        Assert.Equal(0, cloud.Preparations);
        Assert.DoesNotContain("developer-key", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingLocalCredentialDoesNotFallBackToBillableCloudExecution()
    {
        var cloud = new CloudGateway();
        var gateway = new LocalWorkspaceTestRunGateway(() => cloud, () => null, () => false);
        var result = await gateway.PrepareAsync(request);
        Assert.False(result.IsSuccess);
        Assert.Contains("OPENAI_API_KEY", result.Message);
        Assert.Equal(0, cloud.Preparations);
    }

    [Fact]
    public async Task ExplicitCloudSelectionDelegatesToCloudPreparation()
    {
        var cloud = new CloudGateway();
        var gateway = new LocalWorkspaceTestRunGateway(() => cloud, () => "developer-key", () => true);
        var result = await gateway.PrepareAsync(request);
        Assert.False(result.IsSuccess);
        Assert.Equal("Cloud permission denied", result.Message);
        Assert.Equal(1, cloud.Preparations);
        Assert.Null(result.ApiKey);
    }

    private sealed class CloudGateway : IWorkspaceTestRunGateway
    {
        public int Preparations { get; private set; }
        public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest value,
            CancellationToken cancellationToken = default)
        {
            Preparations++;
            return Task.FromResult(WorkspaceTestRunPreparation.Failure("Cloud permission denied"));
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Local run must not be metered.");
    }
}
