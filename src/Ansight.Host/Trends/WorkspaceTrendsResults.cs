namespace Ansight.Host.Trends;

public enum WorkspaceTrendsStatus
{
    Passed,
    Warning,
    Failed,
    Inconclusive,
    Error
}

public enum WorkspaceTrendsHistoryStatus
{
    Warmup,
    Healthy,
    Regressed,
    Recovered,
    Error,
    Suspect
}

public enum WorkspaceTrendsHistoryComparison
{
    UnversionedReference,
    PreviousVersion
}

public sealed record WorkspaceTrendsSpanInstance(
    int InstanceIndex,
    string StartEventId,
    string EndEventId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc)
{
    public string? Group { get; init; }
}

public sealed record WorkspaceTrendsSpanResolution(
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsSpanInstance> Instances);

public sealed record WorkspaceTrendsMetricResult(
    string TrendsId,
    string MetricId,
    string MetricKey,
    int InstanceIndex,
    WorkspaceTrendsStatus Status,
    string Message,
    double? Value,
    string Unit,
    double? BaselineValue,
    int SampleCount,
    DateTimeOffset SpanStartUtc,
    DateTimeOffset SpanEndUtc,
    string MetricDefinitionHash)
{
    public string? SpanGroup { get; init; }
}

public sealed record WorkspaceTrendsCheckResult(
    string TrendsId,
    bool Required,
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsSpanInstance> SpanInstances,
    IReadOnlyList<WorkspaceTrendsMetricResult> Metrics,
    string DefinitionHash);

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

public sealed record WorkspaceTrendsRunContext(
    string EvaluationId,
    string RunId,
    string SessionId,
    string AppId,
    string? AppVersion,
    string? BuildNumber,
    string? Platform,
    string? DeviceModel,
    string? OperatingSystemMajor,
    string? BuildConfiguration,
    DateTimeOffset EvaluatedAtUtc);

public sealed record WorkspaceTrendsReport(
    int SchemaVersion,
    WorkspaceTrendsRunContext Context,
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsCheckResult> Checks,
    IReadOnlyList<WorkspaceTrendsHistoryDecision> History,
    string? SidecarFilePath,
    string? HistoryDatabasePath)
{
    public bool IsSuccess => Status == WorkspaceTrendsStatus.Passed;
}
