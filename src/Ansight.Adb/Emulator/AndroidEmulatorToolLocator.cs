namespace Ansight.Adb;

public static class AndroidEmulatorToolLocator
{
    private static readonly string executableName = OperatingSystem.IsWindows() ? "emulator.exe" : "emulator";

    public static AndroidEmulatorToolResolution Resolve(string? adbPath = null)
    {
        foreach (var candidate in EnumerateCandidates(adbPath))
        {
            if (File.Exists(candidate.Path))
            {
                return AndroidEmulatorToolResolution.Found(
                    Path.GetFullPath(candidate.Path),
                    candidate.Source);
            }
        }

        return AndroidEmulatorToolResolution.NotFound(
            "The Android Emulator executable could not be found. Install the Android Emulator SDK component.");
    }

    public static IReadOnlyList<string> GetCandidatePaths(string? adbPath = null)
        => EnumerateCandidates(adbPath)
            .Select(static candidate => candidate.Path)
            .Distinct(PathComparer)
            .ToArray();

    private static IEnumerable<AndroidEmulatorToolCandidate> EnumerateCandidates(string? adbPath)
    {
        var sdkRootFromAdb = ResolveSdkRootFromAdb(adbPath);
        if (!string.IsNullOrWhiteSpace(sdkRootFromAdb))
        {
            yield return new AndroidEmulatorToolCandidate(
                Path.Combine(sdkRootFromAdb, "emulator", executableName),
                "ADB SDK");
        }

        foreach (var environmentVariable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var sdkPath = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(sdkPath))
            {
                yield return new AndroidEmulatorToolCandidate(
                    Path.Combine(sdkPath.Trim(), "emulator", executableName),
                    environmentVariable);
            }
        }

        foreach (var pathDirectory in EnumeratePathDirectories())
        {
            yield return new AndroidEmulatorToolCandidate(
                Path.Combine(pathDirectory, executableName),
                "PATH");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return new AndroidEmulatorToolCandidate(
                Path.Combine(userProfile, "Library", "Android", "sdk", "emulator", executableName),
                "default-macos-sdk");
            yield return new AndroidEmulatorToolCandidate(
                Path.Combine(userProfile, "Android", "Sdk", "emulator", executableName),
                "default-linux-sdk");
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return new AndroidEmulatorToolCandidate(
                Path.Combine(localApplicationData, "Android", "Sdk", "emulator", executableName),
                "default-windows-sdk");
        }
        foreach (var sdkRoot in AndroidSdkDiscovery.FromPath(Environment.GetEnvironmentVariable("PATH")))
        {
            yield return new AndroidEmulatorToolCandidate(Path.Combine(sdkRoot, "emulator", executableName), "existing-sdk-tool");
        }

        foreach (var sdkRoot in AndroidSdkDiscovery.SystemRoots())
        {
            yield return new AndroidEmulatorToolCandidate(Path.Combine(sdkRoot, "emulator", executableName), "system-sdk");
        }
    }

    private static string? ResolveSdkRootFromAdb(string? adbPath)
    {
        if (string.IsNullOrWhiteSpace(adbPath))
        {
            return null;
        }

        var discoveredRoot = AndroidSdkDiscovery.FromTool(adbPath);
        if (discoveredRoot is not null) return discoveredRoot;

        var normalizedPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(adbPath.Trim().Trim('"')));
        var parent = Directory.GetParent(normalizedPath);
        return parent is not null
               && string.Equals(parent.Name, "platform-tools", StringComparison.OrdinalIgnoreCase)
            ? parent.Parent?.FullName
            : null;
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var value in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return value.Trim('"');
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record AndroidEmulatorToolCandidate(string Path, string Source);
}
