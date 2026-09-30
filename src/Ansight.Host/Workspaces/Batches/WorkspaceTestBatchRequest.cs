namespace Ansight.Host.Workspaces.Batches;

public sealed record WorkspaceTestBatchRequest(
    string WorkspacePath,
    IReadOnlyList<string>? TestIds = null,
    TimeSpan? SessionWaitTimeout = null,
    string Model = "",
    int MaximumTurnsPerInstruction = 64,
    int MaximumRoundTrips = 64,
    int MaximumToolCalls = 512,
    bool ContinueAfterTestFailure = true,
    WorkspaceTestTargetRequest? Target = null,
    Guid? TeamId = null,
    bool EnableWorkspaceTools = true)
{
    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    public string Source { get; init; } = "host";

    public bool CaptureTrace { get; init; }

    public SimulatorAgentOpenAiProtocol OpenAiProtocol { get; init; } = SimulatorAgentOpenAiProtocol.Http;

    public IReadOnlyList<WorkspaceTestTargetRequest>? Targets { get; init; }

    public bool Parallel { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, string?>? SecretResolver { get; init; }
}
