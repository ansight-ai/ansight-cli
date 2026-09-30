namespace Ansight.Host.Replay;

public sealed record SessionTimelineTrimRequest(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Mode);
