namespace Ansight.Host.Profiling;

public sealed record NativeCaptureSnapshot(
    string CaptureId,
    string State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    NativeCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines)
{
    public bool IsTerminal => State is "completed" or "failed" or "cancelled";
}
