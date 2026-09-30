namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetStartupCaptureRequest(
    string ApplicationPath,
    string AppId,
    string DeviceId,
    TimeSpan Duration,
    string? SymbolsPath = null,
    bool Headless = false);
