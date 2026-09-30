namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record DotNetTraceToolchain(
    string? DotNetTracePath,
    string? DotNetDsRouterPath,
    string? AdbPath,
    string? XcrunPath,
    string? SimCtlPath,
    string? DotNetTraceVersion,
    string? DotNetDsRouterVersion,
    string? AdbVersion,
    string? XcodeVersion)
{
    public bool IsAvailable => DotNetTracePath is not null && DotNetDsRouterPath is not null;

    public bool IsAndroidEmulatorAvailable => IsAvailable
                                              && AdbPath is not null;

    public bool IsAndroidDeviceAvailable => IsAndroidEmulatorAvailable;

    public bool IsIosSimulatorAvailable => IsAppleDesktopHost
                                           && IsAvailable
                                           && XcrunPath is not null
                                           && SimCtlPath is not null;

    public bool IsIosDeviceAvailable => IsAppleDesktopHost
                                        && IsAvailable
                                        && XcrunPath is not null;

    private static bool IsAppleDesktopHost => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();
}
