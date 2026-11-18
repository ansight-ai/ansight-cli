namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentProjectCatalog(
    bool IsSuccess,
    string Message,
    IReadOnlyList<RemoteAgentApp> Apps,
    IReadOnlyList<RemoteAgentProject> Projects,
    bool CodexAvailable);
