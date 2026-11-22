namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentChatListRequest(
    string SessionId,
    int Limit = 50,
    string? RememberedTaskId = null);
