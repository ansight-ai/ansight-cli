using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentToolCallAudit(
    int Sequence,
    int InstructionIndex,
    int InstructionTurn,
    string CallId,
    string ToolName,
    bool IsAnsightTool,
    string? CorrelationId,
    DateTimeOffset StartedUtc,
    long DurationMilliseconds,
    SimulatorAgentAuditPayload Arguments,
    SimulatorAgentAuditPayload Result,
    bool IsError,
    string Message)
{
    public IReadOnlyList<RepositoryTaskToolCall>? TaskCalls { get; init; }

    public IReadOnlyList<RepositoryTaskAssertion>? TaskAssertions { get; init; }

    public RepositoryTaskSourceTrace? TaskSource { get; init; }

    public SimulatorAgentOcrTraceEvidence? OcrEvidence { get; init; }

    public SimulatorAgentAccessibilityTraceEvidence? AccessibilityEvidence { get; init; }
}
