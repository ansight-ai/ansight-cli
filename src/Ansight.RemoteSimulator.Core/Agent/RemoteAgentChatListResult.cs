namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentChatListResult(
    bool IsSuccess,
    string Message,
    IReadOnlyList<RemoteAgentChatSession> Sessions);
