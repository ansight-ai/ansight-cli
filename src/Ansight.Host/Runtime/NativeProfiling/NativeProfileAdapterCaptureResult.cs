namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileAdapterCaptureResult(
    IReadOnlyList<NativeProfileProducedArtifact> Artifacts,
    IReadOnlyList<string> Warnings);
