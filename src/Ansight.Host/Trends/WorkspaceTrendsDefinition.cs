namespace Ansight.Host.Trends;

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
