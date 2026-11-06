namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsSeries(
    string SeriesId,
    string AppId,
    string DecisionId,
    string MetricKey,
    WorkspaceTrendsHistoryComparison Comparison,
    string? AppVersion,
    string? BuildNumber,
    string? BaselineAppVersion,
    string? SpanGroup,
    string? Platform,
    string? DeviceModel,
    string? OperatingSystemMajor,
    string? BuildConfiguration,
    string SeriesKey,
    string DefinitionHash,
    string MetricDefinitionHash,
    string Unit,
    WorkspaceTrendsHistoryStatus LatestStatus,
    bool Blocking,
    IReadOnlyList<WorkspaceTrendsHistoryEntry> Points)
{
    public WorkspaceTrendsChartMetadata? Chart { get; init; }
}
