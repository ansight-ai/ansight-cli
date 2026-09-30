namespace Ansight.Adb;

public static class AdbToolLocator
{
    private static readonly string executableName = OperatingSystem.IsWindows() ? "adb.exe" : "adb";

    public static AdbToolResolution Resolve(string? configuredPath = null)
    {
        foreach (var candidate in EnumerateCandidates(configuredPath))
        {
            if (File.Exists(candidate.Path))
            {
                return AdbToolResolution.Found(Path.GetFullPath(candidate.Path), candidate.Source);
            }
        }

        return AdbToolResolution.NotFound(
            "ADB could not be found. Install Android SDK Platform Tools or set ANSIGHT_ADB_PATH to the ADB executable.");
    }

    public static IReadOnlyList<string> GetCandidatePaths(string? configuredPath = null)
        => EnumerateCandidates(configuredPath)
            .Select(candidate => candidate.Path)
            .Distinct(PathComparer)
            .ToArray();

    private static IEnumerable<AdbToolCandidate> EnumerateCandidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return new AdbToolCandidate(NormalizeConfiguredPath(configuredPath), "configured");
        }

        foreach (var environmentVariable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var sdkPath = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(sdkPath))
            {
                yield return new AdbToolCandidate(
                    Path.Combine(sdkPath.Trim(), "platform-tools", executableName),
                    environmentVariable);
            }
        }

        foreach (var pathDirectory in EnumeratePathDirectories())
        {
            yield return new AdbToolCandidate(Path.Combine(pathDirectory, executableName), "PATH");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return new AdbToolCandidate(
                Path.Combine(home, "Library", "Android", "sdk", "platform-tools", executableName),
                "default-macos-sdk");
            yield return new AdbToolCandidate(
                Path.Combine(home, "Android", "Sdk", "platform-tools", executableName),
                "default-linux-sdk");
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return new AdbToolCandidate(
                Path.Combine(localApplicationData, "Android", "Sdk", "platform-tools", executableName),
                "default-windows-sdk");
        }
        foreach (var sdkRoot in AndroidSdkDiscovery.FromPath(Environment.GetEnvironmentVariable("PATH")))
        {
            yield return new AdbToolCandidate(Path.Combine(sdkRoot, "platform-tools", executableName), "existing-sdk-tool");
        }

        foreach (var sdkRoot in AndroidSdkDiscovery.SystemRoots())
        {
            yield return new AdbToolCandidate(Path.Combine(sdkRoot, "platform-tools", executableName), "system-sdk");
        }
    }

    private static string NormalizeConfiguredPath(string configuredPath)
    {
        var path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        if (!Directory.Exists(path))
        {
            return path;
        }

        var directExecutable = Path.Combine(path, executableName);
        return File.Exists(directExecutable)
            ? directExecutable
            : Path.Combine(path, "platform-tools", executableName);
    }

    private static IEnumerable<string> EnumeratePathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var value in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return value.Trim('"');
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record AdbToolCandidate(string Path, string Source);
}
