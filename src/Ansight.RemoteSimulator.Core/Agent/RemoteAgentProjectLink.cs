namespace Ansight.RemoteSimulator.Core.Agent;

public sealed record RemoteAgentProjectLink(
    string ProjectId,
    string ProjectName,
    string? WorkingDirectory,
    DateTimeOffset UpdatedAtUtc);
