namespace Ansight.Host.Apps;

/// <summary>
/// A raw application event received from a connected Ansight SDK session.
/// </summary>
public sealed record RuntimeAppEvent(
    DateTimeOffset OccurredAtUtc,
    string EventId,
    string SessionId,
    string AppId,
    string ClientName,
    string Label,
    string EventType,
    string Details,
    byte ChannelId) : RuntimeEvent(OccurredAtUtc);
