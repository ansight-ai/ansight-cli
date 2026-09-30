namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentSecretMetadata(
    string Alias,
    string AppId,
    string VersionId,
    DateTimeOffset UpdatedUtc);
