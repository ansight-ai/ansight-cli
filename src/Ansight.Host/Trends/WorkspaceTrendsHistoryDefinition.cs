namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryDefinition(
    string DecisionId,
    string MetricKey,
    WorkspaceTrendsHistoryBaselineDefinition Baseline,
    WorkspaceTrendsHistoryRegressionDefinition Regression,
    IReadOnlyList<string> Cohort,
    string DefinitionHash,
    string FilePath)
{
    public string? Platform { get; init; }
    public WorkspaceTrendsChartDefinition? Chart { get; init; }
    public bool Blocking { get; init; }
}
