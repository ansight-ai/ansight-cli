namespace Ansight.Host.Profiling;

public sealed record DotNetProfilingToolchain(
    bool IsAvailable,
    bool IsAndroidAvailable,
    bool IsIosSimulatorAvailable,
    bool IsIosDeviceAvailable,
    string? DotNetTracePath,
    string? DotNetDsRouterPath,
    string? AdbPath,
    string? XcrunPath,
    string? SimCtlPath,
    string? DotNetTraceVersion,
    string? DotNetDsRouterVersion,
    string? AdbVersion,
    string? XcodeVersion);
