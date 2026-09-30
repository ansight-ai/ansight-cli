using System.Text.Json.Serialization;
namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunAudit(
    int SchemaVersion,
    string RunId,
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    SimulatorAgentRunStatus Status,
    string Message,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMilliseconds,
    int MaximumTurnsPerInstruction,
    int MaximumToolCalls,
    int InstructionCount,
    int PassedInstructionCount,
    int FailedInstructionCount,
    int CancelledInstructionCount,
    int ModelPassCount,
    int FailedModelPassCount,
    int FunctionCallCount,
    int AnsightToolCallCount,
    int SuccessfulAnsightToolCallCount,
    int FailedAnsightToolCallCount,
    SimulatorAgentTokenUsage Tokens,
    IReadOnlyList<string> RequestedInstructions,
    IReadOnlyList<SimulatorAgentInstructionResult> Instructions,
    IReadOnlyList<SimulatorAgentModelPassAudit> ModelPasses,
    IReadOnlyList<SimulatorAgentToolCallAudit> ToolCalls)
{
    public IReadOnlyList<SimulatorAgentStartupStep> StartupSteps { get; init; } = [];

    public string? AppId { get; init; }

    public string ExecutionMode { get; init; } = WorkspaceExecutionModes.Sdk;

    public string? ReasoningEffort { get; init; }

    public string? Reasoning { get; init; }

    public string? ReasoningConfigurationRevision { get; init; }

    public bool ContinueAfterInstructionFailure { get; init; }

    public string? AgentPrompt { get; init; }

    public string? PromptCacheKey { get; init; }

    public int? MaximumModelOutputTokens { get; init; }

    public int CompletionGracePassCount { get; init; }

    public string? OpenAiTransport { get; init; }

    public string? OpenAiProtocol { get; init; }

    public SimulatorAgentRunEnvironment? Environment { get; init; }

    public IReadOnlyList<SimulatorAgentSecretReference> SecretReferences { get; init; } = [];

    public string? ReplayedFromRunId { get; init; }

    public int MaximumRoundTrips { get; init; }

    public string? WorkspacePath { get; init; }

    public string? WorkspaceTestId { get; init; }

    public string? WorkspaceTestName { get; init; }

    public string? BatchRunId { get; init; }

    public IReadOnlyList<SimulatorAgentAppGraphPlan> AppGraphPlans { get; init; } = [];

    public bool AppGraphEnabled { get; init; }

    public bool? TraceEnabled { get; init; }

    public IReadOnlyList<SimulatorAgentRepositoryTaskDiscoveryTrace> RepositoryTaskDiscovery { get; init; } = [];

    public SimulatorAgentRunCost? CalculatedCost { get; init; }

    public SimulatorAgentWebSocketOptions? WebSocketOptions { get; init; }

    [JsonIgnore]
    internal IReadOnlyList<SimulatorAgentModelPassUsage>? MeteringResponses { get; init; }

    public IReadOnlyList<SimulatorAgentModelPassUsage> CreateModelPassUsages()
    {
        if (MeteringResponses is not null)
        {
            return MeteringResponses;
        }

        var generatedResponses = ModelPasses
            .Where(static pass => pass.Succeeded && !string.IsNullOrWhiteSpace(pass.ResponseId))
            .Select(pass => new SimulatorAgentModelPassUsage(
                pass.ResponseId!.Trim(),
                string.IsNullOrWhiteSpace(pass.ResponseModel) ? Model : pass.ResponseModel.Trim(),
                string.IsNullOrWhiteSpace(pass.ResponseServiceTier) ? null : pass.ResponseServiceTier.Trim(),
                pass.StartedUtc.AddMilliseconds(Math.Max(0, pass.DurationMilliseconds)),
                pass.Tokens))
            .ToArray();
        return WarmupResponses.Concat(ModelPasses.SelectMany(static pass => pass.Transport?.WarmupResponses ?? []))
            .Concat(generatedResponses)
            .DistinctBy(static usage => usage.ResponseId, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<SimulatorAgentModelPassUsage> WarmupResponses { get; init; } = [];
}
