namespace Ansight.Host.Profiling;

public sealed record DotNetTraceEvidence(
    string ArtifactKind,
    string RelativePath,
    long Length,
    string Sha256);
