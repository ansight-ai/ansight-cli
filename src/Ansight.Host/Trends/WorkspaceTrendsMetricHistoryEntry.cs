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
