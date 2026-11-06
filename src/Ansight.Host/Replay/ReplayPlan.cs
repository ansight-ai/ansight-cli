namespace Ansight.Host.Replay;

/// <summary>
/// A bounded set of semantic instructions derived from recorded interaction evidence.
/// </summary>
public sealed record ReplayPlan(
    string Schema,
    string SourceKind,
    string SourceId,
    string? AppId,
    int SourceEventCount,
    IReadOnlyList<ReplayStep> Steps,
    IReadOnlyList<string> Diagnostics)
{
    public ReplayFrameCadence? FrameCadence { get; init; }
    public IReadOnlyList<string> Instructions => Steps
        .Select(static step => step.Instruction)
        .ToArray();
}
