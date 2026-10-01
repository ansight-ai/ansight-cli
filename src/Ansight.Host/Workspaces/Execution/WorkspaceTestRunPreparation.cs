namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunPreparation(
    bool IsSuccess, string Message, Guid? TrackingRunId, int? MaximumRoundTrips, Guid? TeamId, string? TeamName)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IModelExecutionTransport? ModelTransport { get; init; }
    public IReadOnlyList<SimulatorAgentStartupStep> StartupSteps { get; init; } = [];
    public AgentReasoningConfiguration? ReasoningConfiguration { get; init; }
    public bool UsesExternalTransport => ModelTransport is not null && TrackingRunId.HasValue;
    public bool HasResolvedTeam => TeamId.HasValue && !string.IsNullOrWhiteSpace(TeamName);
    public static WorkspaceTestRunPreparation WithTransport(IModelExecutionTransport transport,
        Guid trackingRunId, int? maximumRoundTrips, Guid teamId, string teamName)
        => new(true, string.Empty, trackingRunId, maximumRoundTrips, teamId, teamName) { ModelTransport = transport };
    public static WorkspaceTestRunPreparation Failure(string message, Guid? teamId = null, string? teamName = null)
        => new(false, string.IsNullOrWhiteSpace(message) ? "The workspace test could not be prepared." : message.Trim(),
            null, null, teamId, string.IsNullOrWhiteSpace(teamName) ? null : teamName.Trim());
}
