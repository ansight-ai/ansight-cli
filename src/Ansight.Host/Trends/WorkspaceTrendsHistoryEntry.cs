namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryEntry(
    string EvaluationId,
    string RunId,
    string SessionId,
    string AppId,
    string? AppVersion,
    string? BuildNumber,
    string DecisionId,
    string MetricKey,
    WorkspaceTrendsHistoryComparison Comparison,
    WorkspaceTrendsHistoryStatus Status,
    double CurrentValue,
    double? BaselineValue,
    double? AbsoluteDelta,
    double? RelativeDeltaPercent,
    int BaselineRunCount,
    int MinimumBaselineRunCount,
    int ConsecutiveBreachCount,
    DateTimeOffset EvaluatedAtUtc)
{
    public string? BaselineAppVersion { get; init; }
    public string? SpanGroup { get; init; }
    public string? Platform { get; init; }
    public string? DeviceModel { get; init; }
    public string? OperatingSystemMajor { get; init; }
    public string? BuildConfiguration { get; init; }
    public string DefinitionHash { get; init; } = string.Empty;

    public string MetricDefinitionHash { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool Blocking { get; init; }
    public string SeriesKey { get; init; } = string.Empty;
}
