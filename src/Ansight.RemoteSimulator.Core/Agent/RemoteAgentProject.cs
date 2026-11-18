namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentProject(
    string Id,
    string Name,
    IReadOnlyList<string> Roots);
