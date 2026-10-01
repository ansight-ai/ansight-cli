using Ansight.Host.Workspaces.Cloud;
using Ansight.Host.Workspaces.Execution;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class CloudWorkspaceTestRunGatewayTests
{
    [Fact]
    public async Task PreparationDelegatesToCloudOnlyWhenRunStarts()
    {
        var cloud = new RecordingGateway();
        var gateway = new CloudWorkspaceTestRunGateway(() => cloud);
        Assert.Equal(0, cloud.Preparations);

        var result = await gateway.PrepareAsync(new WorkspaceTestRunPreparationRequest(
            null, "/workspace", "test", "Test", "com.example.app", "test-model", 1, 1));

        Assert.False(result.IsSuccess);
        Assert.Equal("Cloud permission denied", result.Message);
        Assert.Equal(1, cloud.Preparations);
    }

    private sealed class RecordingGateway : IWorkspaceTestRunGateway
    {
        public int Preparations { get; private set; }

        public Task<WorkspaceTestRunPreparation> PrepareAsync(
            WorkspaceTestRunPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            Preparations++;
            return Task.FromResult(WorkspaceTestRunPreparation.Failure("Cloud permission denied"));
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(
            WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No run was prepared.");
    }
}
