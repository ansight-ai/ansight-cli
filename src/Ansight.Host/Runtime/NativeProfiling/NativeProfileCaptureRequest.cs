namespace Ansight.Host.Runtime.NativeProfiling;

internal sealed record NativeProfileCaptureRequest(
    string Platform,
    string ApplicationPath,
    string AppId,
    string DeviceId,
    string Preset,
    TimeSpan Duration,
    bool Headless = false);
