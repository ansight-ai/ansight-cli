namespace Ansight.Host.Replay;

public sealed record SessionTimelineExtractRequest(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? Name = null);
