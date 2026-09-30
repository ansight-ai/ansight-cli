namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRepositoryTaskDiscoveryTrace(
    string Stage,
    string? Query,
    IReadOnlyList<SimulatorAgentRepositoryTaskCandidateTrace> Candidates)
{
    public int InstructionIndex { get; init; }

    public int? AvailableTaskCount { get; init; }

    public IReadOnlyList<string> AvailableTaskIds { get; init; } = [];

    public bool DiagnosticsTruncated { get; init; }

    public IReadOnlyList<string> SelectedTaskIds { get; init; } = [];

    public string? Message { get; init; }
}
