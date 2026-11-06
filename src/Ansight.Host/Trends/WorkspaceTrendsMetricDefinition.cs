namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsMetricDefinition(
    string MetricId,
    WorkspaceTrendsMetricSelector Channel,
    string Statistic,
    WorkspaceTrendsBudget Budget,
    double? StatisticThreshold,
    int MinimumSamples,
    TimeSpan MaximumSampleGap,
    TimeSpan BaselineBeforeStart,
    TimeSpan TailAfterEnd)
{
    public string? Platform { get; init; }
    public WorkspaceTrendsRegressionPolicy? Regression { get; init; }
    public WorkspaceTrendsDisplayDefinition? Display { get; init; }
}
