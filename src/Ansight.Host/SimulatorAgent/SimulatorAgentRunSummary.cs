using System.Text.Json.Serialization;

namespace Ansight.Host.SimulatorAgent;

internal sealed record SimulatorAgentRunSummary(
    string RunId,
    string SessionId,
    string? AppId,
    string? WorkspacePath,
    string? WorkspaceTestId,
    string? WorkspaceTestName,
    string? BatchRunId,
    string Model,
    SimulatorAgentRunStatus Status,
    string Message,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMilliseconds,
    SimulatorAgentTokenUsage Tokens,
    int ModelPassCount,
    int AnsightToolCallCount,
    IReadOnlyList<string> RequestedInstructions,
    string? Reasoning,
    SimulatorAgentRunCost? CalculatedCost)
{
    public string ExecutionKind { get; init; } = "agent";

    [JsonIgnore]
    public string FilePath { get; init; } = string.Empty;

    public static SimulatorAgentRunSummary FromEntry(SimulatorAgentRunHistoryEntry entry)
    {
        var audit = entry.Audit;

        return new SimulatorAgentRunSummary(
            audit.RunId,
            audit.SessionId,
            audit.AppId,
            audit.WorkspacePath,
            audit.WorkspaceTestId,
            audit.WorkspaceTestName,
            audit.BatchRunId,
            audit.Model,
            audit.Status,
            audit.Message,
            audit.StartedUtc,
            audit.CompletedUtc,
            audit.DurationMilliseconds,
            audit.Tokens,
            audit.ModelPassCount,
            audit.AnsightToolCallCount,
            audit.RequestedInstructions,
            audit.Reasoning,
            audit.CalculatedCost)
        {
            ExecutionKind = audit.ExecutionKind,
            FilePath = entry.FilePath
        };
    }
}
