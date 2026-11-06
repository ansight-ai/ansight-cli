namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunHistoryEntry(
    SimulatorAgentRunAudit Audit,
    string FilePath);
