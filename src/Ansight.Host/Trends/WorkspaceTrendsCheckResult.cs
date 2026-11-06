namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsCheckResult(
    string TrendsId,
    bool Required,
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsSpanInstance> SpanInstances,
    IReadOnlyList<WorkspaceTrendsMetricResult> Metrics,
    string DefinitionHash);
