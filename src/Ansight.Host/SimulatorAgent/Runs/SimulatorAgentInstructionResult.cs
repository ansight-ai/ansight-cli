namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentInstructionResult(
    int Index,
    string Instruction,
    SimulatorAgentInstructionStatus Status,
    string Summary,
    int Turns,
    int ToolCalls);
