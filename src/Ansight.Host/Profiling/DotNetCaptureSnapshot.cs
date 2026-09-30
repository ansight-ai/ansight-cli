namespace Ansight.Host.Profiling;

public sealed record DotNetCaptureSnapshot(
    string CaptureId,
    string State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    DotNetCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines)
{
    public bool IsTerminal => State is "completed" or "failed" or "cancelled";
}
