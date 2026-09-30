namespace Ansight.Host.Models.Metrics;



public sealed record MetricChartPreparedSeries(
    byte ChannelId,
    bool IsFpsChannel,
    SessionMetricSample[] VisibleSamples,
    SessionMetricSample[] RenderSamples,
    IReadOnlyList<MetricChartPreparedSegment> VisibleSegments,
    IReadOnlyList<MetricChartPreparedSegment> RenderSegments);
