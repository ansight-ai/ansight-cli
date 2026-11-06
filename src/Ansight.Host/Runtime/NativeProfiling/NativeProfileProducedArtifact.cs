namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileProducedArtifact(
    string Kind,
    string Path,
    bool IsDirectory,
    bool Authoritative);
