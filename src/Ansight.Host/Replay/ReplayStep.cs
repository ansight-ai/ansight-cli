namespace Ansight.Host.Replay;

public sealed record ReplayStep(
    int Sequence,
    string Kind,
    string Instruction,
    DateTimeOffset? CapturedAtUtc = null,
    string? Target = null)
{
    public ReplaySourceEvidence? SourceEvidence { get; init; }
}
