namespace Ansight.Host.Profiling;

public sealed record NativeProfileArtifactLocation(
    string CaptureId,
    string Platform,
    string Path,
    NativeCaptureArtifact Artifact);
