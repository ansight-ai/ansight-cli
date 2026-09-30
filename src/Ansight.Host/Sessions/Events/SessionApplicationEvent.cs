namespace Ansight.Host.Models.Session;

public sealed record SessionApplicationEvent(
    string EventId,
    string Label,
    string EventType,
    string Details,
    DateTimeOffset CapturedAtUtc,
    byte ChannelId);
