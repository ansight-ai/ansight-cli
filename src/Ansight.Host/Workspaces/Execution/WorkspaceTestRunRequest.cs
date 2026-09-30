namespace Ansight.Host.Workspaces.Execution;

public sealed record WorkspaceTestRunRequest(
    string WorkspacePath,
    string TestId,
    string? SessionId = null,
    TimeSpan? SessionWaitTimeout = null,
    string Model = "",
    int MaximumTurnsPerInstruction = 64,
    int MaximumRoundTrips = 64,
    int MaximumToolCalls = 512,
    bool ContinueAfterInstructionFailure = true,
    WorkspaceTestTargetRequest? Target = null,
    Guid? TeamId = null,
    bool EnableWorkspaceTools = true)
{
    internal Ansight.Host.Runtime.Operations.OperationExecutionContext? OperationContext { get; init; }

    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    public string? BatchRunId { get; init; }

    public bool CaptureTrace { get; init; }

    public SimulatorAgentOpenAiProtocol OpenAiProtocol { get; init; } = SimulatorAgentOpenAiProtocol.Http;

    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, string?>? SecretResolver { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal WorkspaceTestRunStartParticipant? StartParticipant { get; init; }
}
