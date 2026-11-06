namespace Ansight.Host.Runtime.NativeProfiling;

internal interface INativeProfileCaptureAdapter
{
    string Platform { get; }
    string Engine { get; }

    bool SupportsPreset(string preset);

    Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken);

    Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        NativeProfileAdapterContext context,
        CancellationToken cancellationToken);
}
