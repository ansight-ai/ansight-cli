using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestRunHistorySummary(
    string RunId,
    string? BatchRunId,
    string? WorkspacePath,
    string? TestId,
    string? TestName,
    string DisplayName,
    string? AppId,
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    string Model,
    string Status,
    string Message,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMilliseconds,
    int TotalTokens,
    int ModelPassCount,
    int AnsightToolCallCount,
    string FilePath)
{
    public string? Reasoning { get; init; }
    public string ExecutionKind { get; init; } = "agent";

    public SimulatorAgentRunCost? CalculatedCost { get; init; }
}
