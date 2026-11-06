namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryDecision(
    string DecisionId,
    string MetricKey,
    WorkspaceTrendsHistoryComparison Comparison,
    WorkspaceTrendsHistoryStatus Status,
    string Message,
    double CurrentValue,
    double? BaselineValue,
    double? AbsoluteDelta,
    double? RelativeDeltaPercent,
    int BaselineRunCount,
    int MinimumBaselineRunCount,
    int ConsecutiveBreachCount,
    string DefinitionHash)
{
    public string? BaselineAppVersion { get; init; }
    public string? SpanGroup { get; init; }
    public string MetricDefinitionHash { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public bool Blocking { get; init; }
    public string SeriesKey { get; init; } = string.Empty;
}
