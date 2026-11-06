namespace Ansight.Host.Trends;

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
