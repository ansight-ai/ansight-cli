namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileCaptureSnapshot(
    string CaptureId,
    NativeProfileCaptureState State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    NativeProfileCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines)
{
    public bool IsTerminal => State is NativeProfileCaptureState.Completed
        or NativeProfileCaptureState.Failed
        or NativeProfileCaptureState.Cancelled;
}
