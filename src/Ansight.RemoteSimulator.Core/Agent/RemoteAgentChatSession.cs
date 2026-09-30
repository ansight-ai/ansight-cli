namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentChatSession(
    string Provider,
    string SessionId,
    string Title,
    string? WorkingDirectory,
    DateTimeOffset UpdatedAtUtc,
    string Status,
    bool IsNewSession = false,
    bool IsPinned = false);
