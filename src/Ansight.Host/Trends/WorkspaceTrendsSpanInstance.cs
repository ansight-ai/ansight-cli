namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsSpanInstance(
    int InstanceIndex,
    string StartEventId,
    string EndEventId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc)
{
    public string? Group { get; init; }
}
