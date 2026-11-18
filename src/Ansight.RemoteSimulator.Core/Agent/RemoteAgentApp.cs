namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentApp(
    string AppId,
    string Name,
    string? CodebasePath,
    RemoteAgentProjectLink? Project);
