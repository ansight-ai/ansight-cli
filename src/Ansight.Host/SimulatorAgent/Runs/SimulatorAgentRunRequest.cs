namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunRequest(
    string SessionId,
    IReadOnlyList<string> Instructions,
    string Model = "gpt-6-luna",
    int MaximumTurnsPerInstruction = 64,
    int MaximumToolCalls = 512,
    string? AppId = null,
    bool ContinueAfterInstructionFailure = true)
{
    public IReadOnlyList<SimulatorAgentStartupStep> StartupSteps { get; init; } = [];

    internal Ansight.Host.Runtime.Operations.OperationExecutionContext? OperationContext { get; init; }

    public string Reasoning { get; init; } = AgentReasoningModes.Fast;

    public string ReasoningEffort { get; init; } = "medium";

    public string? ReasoningConfigurationRevision { get; init; }

    public int MaximumInstructionCharacters { get; init; } = SimulatorAgentService.MaximumInstructionCharacters;

    public int? MaximumModelOutputTokens { get; init; }


    [System.Text.Json.Serialization.JsonIgnore]
    public IModelExecutionTransport? ModelTransport { get; init; }

    public Guid? TrackingRunId { get; init; }

    public IReadOnlyList<string> SecretAliases { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, string?>? SecretResolver { get; init; }

    public string? ReplayedFromRunId { get; init; }

    public int MaximumRoundTrips { get; init; } = 64;

    public string? TargetDeviceIdentifier { get; init; }

    public string? WorkspacePath { get; init; }

    public string? WorkspaceTestId { get; init; }

    public string? WorkspaceTestName { get; init; }

    internal IReadOnlyList<string> PreferredTaskIds { get; init; } = [];

    public string? BatchRunId { get; init; }

    public IReadOnlyList<SimulatorAgentAppGraphPlan> AppGraphPlans { get; init; } = [];

    public bool AppGraphEnabled { get; init; }

    public string? AppGraphExplorationName { get; init; }

    public bool CaptureTrace { get; init; }

    public SimulatorAgentOpenAiProtocol OpenAiProtocol { get; init; } = SimulatorAgentOpenAiProtocol.Http;

    public SimulatorAgentWebSocketOptions? WebSocketOptions { get; init; }
}
