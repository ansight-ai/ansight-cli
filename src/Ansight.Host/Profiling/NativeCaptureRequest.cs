namespace Ansight.Host.Profiling;

public sealed record NativeCaptureRequest(
    string Platform,
    string ApplicationPath,
    string AppId,
    string DeviceId,
    string Preset,
    TimeSpan Duration,
    bool Headless = false);
