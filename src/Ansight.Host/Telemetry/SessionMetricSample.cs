namespace Ansight.Host.Models.Metrics;

public sealed class SessionMetricSample
{
    public required byte ChannelId { get; init; }
    public required double Value { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public int SegmentId { get; init; }
}
