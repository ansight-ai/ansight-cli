namespace Ansight.Host.Profiling;

public sealed record DotNetCaptureArtifactLocation(
    string CaptureId,
    string FilePath,
    DotNetCaptureArtifact Artifact,
    bool Authoritative);
