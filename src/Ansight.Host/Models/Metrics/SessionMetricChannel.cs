namespace Ansight.Host.Models.Metrics;

public sealed class SessionMetricChannel
{
    public required byte ChannelId { get; init; }
    public required string Name { get; init; }
    public required string ColorHex { get; init; }
    public string? Unit { get; init; }
    public string Type { get; init; } = "custom";
    public string? Source { get; init; }
    public string? Group { get; init; }
    public string? Kind { get; init; }
}
