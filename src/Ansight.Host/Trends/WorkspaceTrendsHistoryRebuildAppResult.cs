namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryRebuildAppResult(
    string AppId,
    string WorkspacePath,
    int MetricCount,
    int HistoryDefinitionCount,
    int HistoryResultCount)
{
    public IReadOnlyDictionary<WorkspaceTrendsHistoryStatus, int> StatusCounts { get; init; } = new Dictionary<WorkspaceTrendsHistoryStatus, int>();
}
