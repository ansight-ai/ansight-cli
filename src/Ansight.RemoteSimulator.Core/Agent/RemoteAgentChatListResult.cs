namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentChatListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<RemoteAgentChatSession> Sessions,
    string? AppId = null,
    RemoteAgentProjectLink? Project = null,
    string? PreferredSessionId = null,
    bool SupportsRecordingHandoff = false);
