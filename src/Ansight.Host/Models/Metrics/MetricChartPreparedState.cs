namespace Ansight.Host.Models.Metrics;



public sealed record MetricChartPreparedState(
    DateTimeOffset EarliestVisibleUtc,
    DateTimeOffset LatestVisibleUtc,
    IReadOnlyList<MetricChartPreparedSeries> Series,
    bool HasMemoryMetrics,
    bool HasFpsMetrics,
    double MaxMemoryDisplayValue,
    double MaxFpsDisplayValue)
{
    public static MetricChartPreparedState Empty { get; } = new(
        DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        Array.Empty<MetricChartPreparedSeries>(),
        false,
        false,
        1d,
        1d);
}
