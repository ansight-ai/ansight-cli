namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAuditPersistenceResult(
    string? FilePath,
    string? ErrorMessage)
{
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}
