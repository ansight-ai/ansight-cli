using Ansight.Host.SimulatorAgent.OpenAi;
using Ansight.Host.SimulatorAgent.OpenAi.Transport;

namespace Ansight.Host.Workspaces.Cloud;

/// <summary>Uses this machine's OpenAI key when configured, otherwise resolves the cloud gateway.</summary>
internal sealed class CloudWorkspaceTestRunGateway : IWorkspaceTestRunGateway
{
    private readonly Func<IWorkspaceTestRunGateway> cloudGateway;
    private readonly LocalOpenAiCredentialStore localCredentials;

    public CloudWorkspaceTestRunGateway(
        Func<IWorkspaceTestRunGateway> cloudGateway,
        LocalOpenAiCredentialStore localCredentials)
    {
        this.cloudGateway = cloudGateway;
        this.localCredentials = localCredentials;
    }

    public Task<WorkspaceTestRunPreparation> PrepareAsync(WorkspaceTestRunPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Explicit caller-scoped Ansight credentials retain their own access and billing scope.
        var hasCallerCredentials = RunnerAiCredentialContext.Current is not null
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_RUNNER_API_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSIGHT_API_KEY"));
        var apiKey = hasCallerCredentials ? null : localCredentials.Read();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var preparation = new WorkspaceTestRunPreparation(true, string.Empty, null, null, request.TeamId, null)
            {
                ModelTransport = new LocalOpenAiTransport(apiKey),
                ReasoningConfiguration = AgentReasoningConfiguration.CreateDefault(request.Reasoning, request.Model)
            };

            return Task.FromResult(preparation);
        }

        return cloudGateway().PrepareAsync(request, cancellationToken);
    }

    // Completion is only invoked for preparations with a cloud tracking run ID.
    public Task<WorkspaceTestRunMeterResult> CompleteAsync(WorkspaceTestRunMeterCompletion completion,
        CancellationToken cancellationToken = default) => cloudGateway().CompleteAsync(completion, cancellationToken);
}
