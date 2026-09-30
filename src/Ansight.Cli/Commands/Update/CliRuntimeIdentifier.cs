using System.Runtime.InteropServices;

namespace Ansight.Cli.Commands.Update;

internal static class CliRuntimeIdentifier
{
    public static string Current
    {
        get
        {
            var platform = OperatingSystem.IsWindows()
                ? "win"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : OperatingSystem.IsLinux()
                        ? "linux"
                        : throw new PlatformNotSupportedException(
                            "Ansight CLI updates support Windows, macOS, and Linux.");
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException(
                    $"Ansight CLI updates do not support {RuntimeInformation.ProcessArchitecture}.")
            };
            return $"{platform}-{architecture}";
        }
    }
}
