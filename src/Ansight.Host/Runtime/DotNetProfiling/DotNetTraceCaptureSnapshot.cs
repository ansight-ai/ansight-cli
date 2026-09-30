namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceCaptureSnapshot(
    string CaptureId,
    DotNetTraceCaptureState State,
    string Phase,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    string? FailureMessage,
    DotNetTraceCaptureManifest Manifest,
    IReadOnlyList<string> RecentLogLines);
