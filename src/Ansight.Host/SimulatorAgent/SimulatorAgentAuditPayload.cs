namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAuditPayload(
    string Content,
    int OriginalCharacterCount,
    bool WasTruncated,
    string Sha256);
