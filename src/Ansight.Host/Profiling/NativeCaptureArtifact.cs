namespace Ansight.Host.Profiling;

public sealed record NativeCaptureArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256,
    bool IsDirectory,
    bool Authoritative);
