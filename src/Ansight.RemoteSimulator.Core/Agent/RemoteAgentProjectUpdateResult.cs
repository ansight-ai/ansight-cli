namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentProjectUpdateResult(
    bool IsSuccess,
    string Message,
    RemoteAgentProjectLink? Project = null);
