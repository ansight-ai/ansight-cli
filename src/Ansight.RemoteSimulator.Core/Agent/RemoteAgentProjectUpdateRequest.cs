namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentProjectUpdateRequest(
    string AppId,
    string? ProjectId,
    string? WorkingDirectory = null);
