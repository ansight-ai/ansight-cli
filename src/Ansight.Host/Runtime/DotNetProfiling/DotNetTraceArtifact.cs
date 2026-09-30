namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256);
