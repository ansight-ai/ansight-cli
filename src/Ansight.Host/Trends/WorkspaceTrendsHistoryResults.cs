namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsMetricHistoryEntry(
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
    string TrendsId,
    int InstanceIndex,
    string MetricId,
    string MetricKey,
    double Value,
    string Unit,
    WorkspaceTrendsStatus Status,
    DateTimeOffset EvaluatedAtUtc)
{
    public string? SpanGroup { get; init; }

    public string MetricDefinitionHash { get; init; } = string.Empty;
}

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

public sealed record WorkspaceTrendsChartMetadata(
    string DecisionId,
    string MetricKey,
    string? Title,
    WorkspaceTrendsChartAxisDefinition YAxis)
{
    public string DefinitionHash { get; init; } = string.Empty;
}

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

public sealed record WorkspaceTrendsHistoryResult(
    string DatabasePath,
    IReadOnlyList<WorkspaceTrendsMetricHistoryEntry> Metrics,
    IReadOnlyList<WorkspaceTrendsHistoryEntry> History)
{
    public IReadOnlyList<WorkspaceTrendsChartMetadata> Charts { get; init; } = [];

    public IReadOnlyList<WorkspaceTrendsSeries> Series { get; init; } = [];
}

public sealed record WorkspaceTrendsHistoryRebuildRequest(
    string? AppId = null,
    string? AppVersion = null,
    string? MetricKey = null,
    string? WorkspacePath = null,
    bool DryRun = false);

public sealed record WorkspaceTrendsHistoryRebuildAppResult(
    string AppId,
    string WorkspacePath,
    int MetricCount,
    int HistoryDefinitionCount,
    int HistoryResultCount)
{
    public IReadOnlyDictionary<WorkspaceTrendsHistoryStatus, int> StatusCounts { get; init; }
        = new Dictionary<WorkspaceTrendsHistoryStatus, int>();
}

public sealed record WorkspaceTrendsHistoryRebuildSkippedApp(
    string AppId,
    string Reason);

public sealed record WorkspaceTrendsHistoryRebuildResult(
    string DatabasePath,
    bool DryRun,
    string? AppId,
    string? AppVersion,
    string? MetricKey,
    int RemovedHistoryResultCount,
    int RebuiltHistoryResultCount,
    IReadOnlyList<WorkspaceTrendsHistoryRebuildAppResult> Apps,
    IReadOnlyList<WorkspaceTrendsHistoryRebuildSkippedApp> SkippedApps);
