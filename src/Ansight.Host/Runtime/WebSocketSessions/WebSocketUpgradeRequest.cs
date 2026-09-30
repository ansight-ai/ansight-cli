namespace Ansight.Host.Runtime.WebSocketSessions;



internal sealed record WebSocketUpgradeRequest(
    string Path,
    string? Token,
    string WebSocketKey);
