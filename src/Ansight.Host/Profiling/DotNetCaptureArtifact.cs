namespace Ansight.Host.Profiling;

public sealed record DotNetCaptureArtifact(
    string Kind,
    string RelativePath,
    long Length,
    string Sha256);
