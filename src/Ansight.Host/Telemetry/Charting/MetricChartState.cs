namespace Ansight.Host.Models.Metrics;

public sealed record MetricChartState(
    IReadOnlyList<SessionMetricSample> Metrics,
    IReadOnlyDictionary<byte, SessionMetricChannel> Channels,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset SessionEndUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsLive)
{
    public static MetricChartState Empty { get; } = new(
        Array.Empty<SessionMetricSample>(),
        new Dictionary<byte, SessionMetricChannel>(),
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        false);
}
