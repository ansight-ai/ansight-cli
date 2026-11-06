namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryResult(
    string DatabasePath,
    IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> Metrics,
    IReadOnlyList<WorkspaceTrendsHistoryEntry> History)
{
    public IReadOnlyList<WorkspaceTrendsChartMetadata> Charts { get; init; } = [];

    public IReadOnlyList<WorkspaceTrendsSeries> Series { get; init; } = [];
}
