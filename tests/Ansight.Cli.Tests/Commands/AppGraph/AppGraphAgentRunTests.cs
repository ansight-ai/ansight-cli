using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Tests.Commands.AppGraph;

public sealed class AppGraphAgentRunTests
{

    [Fact]
    public async Task PrepareAppGraphAgentRunAsyncRejectsMissingCloudGateway()
    {
        var result = await AppGraphCommands.PrepareAppGraphAgentRunAsync(
            gateway: null,
            "com.example.app",
            "gpt-5.6-terra",
            "/workspace/app-graph-executions",
            teamId: null,
            "app-graph-explore",
            "App Graph exploration",
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("cloud gateway", result.Message);
        Assert.False(result.UsesExternalTransport);
    }

    private sealed class RecordingWorkspaceTestRunGateway : IWorkspaceTestRunGateway
    {
        private readonly WorkspaceTestRunPreparation preparation;

        public RecordingWorkspaceTestRunGateway(WorkspaceTestRunPreparation preparation)
        {
            this.preparation = preparation;
        }

        public WorkspaceTestRunPreparationRequest? PreparationRequest { get; private set; }

        public Task<WorkspaceTestRunPreparation> PrepareAsync(
            WorkspaceTestRunPreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparationRequest = request;
            return Task.FromResult(preparation);
        }

        public Task<WorkspaceTestRunMeterResult> CompleteAsync(
            WorkspaceTestRunMeterCompletion completion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WorkspaceTestRunMeterResult.Success());
        }
    }
}
