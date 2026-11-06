namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryBaselineDefinition(
    string Strategy,
    int Runs,
    int MinimumRuns);
