namespace Ansight.Host.Models.Metrics;

public sealed class SessionMetricSample
{
    public required byte ChannelId { get; init; }
    public required long Value { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public int SegmentId { get; init; }
}
