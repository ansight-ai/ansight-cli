namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileAdapterContext(
    string CaptureId,
    string CapturePath,
    string CaptureToolPath,
    string ApplicationPath,
    string AppId,
    string DeviceId,
    string Preset,
    TimeSpan Duration,
    IDeviceService Devices,
    Action<string> Log);

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
