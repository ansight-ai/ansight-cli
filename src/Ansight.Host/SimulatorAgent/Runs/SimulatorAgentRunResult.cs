namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentRunResult(
    SimulatorAgentRunStatus Status,
    string Message,
    IReadOnlyList<SimulatorAgentInstructionResult> Instructions,
    int TotalTurns,
    int TotalToolCalls,
    int InputTokens,
    int OutputTokens,
    TimeSpan Duration,
    SimulatorAgentRunAudit Audit,
    string? AuditFilePath,
    string? AuditPersistenceError);
