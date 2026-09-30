namespace Ansight.Host.Profiling;

public sealed record DotNetCaptureRequest(
    string ApplicationPath,
    string AppId,
    string DeviceId,
    TimeSpan Duration,
    string? SymbolsPath = null,
    bool Headless = false);
