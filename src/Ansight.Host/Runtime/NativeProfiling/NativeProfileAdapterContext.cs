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
