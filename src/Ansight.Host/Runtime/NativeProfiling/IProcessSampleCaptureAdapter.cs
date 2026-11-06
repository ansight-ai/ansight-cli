namespace Ansight.Host.Runtime.NativeProfiling;

internal interface IProcessSampleCaptureAdapter
{
    string Engine { get; }

    Task<NativeProfileToolchain> GetToolchainAsync(CancellationToken cancellationToken);

    Task<NativeProfileAdapterCaptureResult> CaptureAsync(
        ProcessSampleAdapterContext context,
        CancellationToken cancellationToken);
}
