namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRepositoryTaskCandidateTrace(
    string TaskId,
    double? Score,
    double? Coverage,
    IReadOnlyList<string> Reasons)
{
    public IReadOnlyList<string> MatchedQueryTerms { get; init; } = [];

    public IReadOnlyList<string> UnmatchedQueryTerms { get; init; } = [];
}
