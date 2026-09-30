namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentTaskListRequest(
    string? SessionId = null,
    int Limit = 100);
