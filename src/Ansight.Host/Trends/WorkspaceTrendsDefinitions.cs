namespace Ansight.Host.Trends;

public enum WorkspaceTrendsSpanSelection
{
    FirstCompleted,
    LastCompleted,
    ExactlyOne,
    All
}

public sealed record WorkspaceEventAnchor(
    string Label,
    string? EventType = null,
    byte? ChannelId = null)
{
    // Keep hashes of existing event-only definitions stable.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceTrendsLogSelector? Log { get; init; }
}

public sealed record WorkspaceTrendsLogSelector(
    string StreamId,
    string Message,
    string Match = "exact",
    string? Priority = null,
    string? Source = null,
    string? Tag = null,
    int? ProcessId = null);

public sealed record WorkspaceTrendsSpanDefinition(
    WorkspaceEventAnchor Start,
    WorkspaceEventAnchor End,
    WorkspaceTrendsSpanSelection Selection,
    TimeSpan MaximumDuration);

public sealed record WorkspaceTrendsMetricSelector(
    string? Type,
    string? Name,
    string? Source,
    string? Kind,
    bool RequireExactlyOne);

public sealed record WorkspaceTrendsBudget(
    double? GreaterThanOrEqual,
    double? LessThanOrEqual,
    double? AbsoluteLessThanOrEqual);

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

public enum WorkspaceTrendsRegressionDirection
{
    Higher,
    Lower
}

public sealed record WorkspaceTrendsRegressionPolicy(
    WorkspaceTrendsRegressionDirection Direction,
    double? Percent,
    double? Absolute,
    int ConfirmRuns,
    int BaselineRuns,
    int MinimumBaselineRuns,
    IReadOnlyList<string> SeriesBy,
    bool Blocking);

public sealed record WorkspaceTrendsDisplayDefinition(
    string? Title,
    string? Unit,
    double? Scale,
    int? FractionDigits,
    double? Minimum,
    double? Maximum,
    bool? IncludeZero);

public sealed record WorkspaceTrendsDefinition(
    string TrendsId,
    string AppId,
    WorkspaceTrendsSpanDefinition Span,
    bool Required,
    string MissingDataOutcome,
    IReadOnlyList<WorkspaceTrendsMetricDefinition> Metrics,
    string DefinitionHash,
    string FilePath)
{
    public bool Enabled { get; init; } = true;

    public TimeSpan ObservationTail => Metrics.Count == 0
        ? TimeSpan.Zero
        : Metrics.Max(static metric => metric.TailAfterEnd);
}

public sealed record WorkspaceTrendsHistoryBaselineDefinition(
    string Strategy,
    int Runs,
    int MinimumRuns);

public sealed record WorkspaceTrendsHistoryRegressionDefinition(
    double? RelativeIncreasePercent,
    double? RelativeDecreasePercent,
    double? AbsoluteIncrease,
    double? AbsoluteDecrease,
    int ConsecutiveRuns)
{
    public bool RequireAllThresholds { get; init; } = true;
}

public sealed record WorkspaceTrendsChartAxisDefinition(
    string? Unit,
    double? Scale,
    int? FractionDigits,
    double? Minimum,
    double? Maximum,
    bool? IncludeZero);

public sealed record WorkspaceTrendsChartDefinition(
    string? Title,
    WorkspaceTrendsChartAxisDefinition YAxis);

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

public sealed record WorkspaceTrendsCatalogResult(
    string WorkspacePath,
    IReadOnlyList<WorkspaceTrendsDefinition> Definitions,
    IReadOnlyList<WorkspaceTrendsHistoryDefinition> HistoryDefinitions,
    IReadOnlyList<string> Warnings)
{
    public WorkspaceTrendsDefinition? FindDefinition(string trendsId) => Definitions.FirstOrDefault(
        definition => string.Equals(
            definition.TrendsId,
            trendsId,
            StringComparison.OrdinalIgnoreCase));
}
