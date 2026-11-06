namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256,
    bool IsDirectory,
    bool Authoritative);
