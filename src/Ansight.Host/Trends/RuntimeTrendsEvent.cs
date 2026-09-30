using Ansight.Host.Trends;

namespace Ansight.Host.Trends;

public enum RuntimeTrendsEventKind
{
    CheckFailed,
    RegressionDetected,
    RegressionRecovered
}

public sealed record RuntimeTrendsEvent(
    DateTimeOffset OccurredAtUtc,
    RuntimeTrendsEventKind Kind,
    string SessionId,
    string AppId,
    string DefinitionId,
    string Message,
    WorkspaceTrendsStatus? TrendsStatus = null,
    WorkspaceTrendsHistoryStatus? HistoryStatus = null) : RuntimeEvent(OccurredAtUtc)
{
    public string? SpanGroup { get; init; }

    public WorkspaceTrendsHistoryComparison? HistoryComparison { get; init; }
}
