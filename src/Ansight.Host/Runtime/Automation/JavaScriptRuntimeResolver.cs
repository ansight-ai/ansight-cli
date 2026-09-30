namespace Ansight.Host.Runtime.Automation;

internal static class JavaScriptRuntimeResolver
{
    private static readonly string[] macFallbackPaths =
    [
        "/opt/homebrew/bin/node",
        "/usr/local/bin/node",
        "/usr/bin/node"
    ];

    public static JavaScriptRuntimeResolution Resolve(
        string configuredPath,
        string? inheritedPath = null,
        IReadOnlyList<string>? platformFallbackPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        var normalizedConfiguredPath = configuredPath.Trim();
        if (HasDirectoryComponent(normalizedConfiguredPath))
        {
            var absolutePath = Path.GetFullPath(normalizedConfiguredPath);
            return File.Exists(absolutePath)
                ? Available(absolutePath)
                : Missing(absolutePath);
        }

        foreach (var candidate in EnumerateCandidates(
                     normalizedConfiguredPath,
                     inheritedPath ?? Environment.GetEnvironmentVariable("PATH"),
                     platformFallbackPaths ?? GetPlatformFallbackPaths()))
        {
            if (File.Exists(candidate))
            {
                return Available(Path.GetFullPath(candidate));
            }
        }

        return Missing(normalizedConfiguredPath);
    }

    private static IEnumerable<string> EnumerateCandidates(
        string executableName,
        string? inheritedPath,
        IReadOnlyList<string> platformFallbackPaths)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directoryPath in (inheritedPath ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in AddPlatformExecutableExtensions(Path.Combine(directoryPath, executableName)))
            {
                if (candidates.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        foreach (var fallbackPath in platformFallbackPaths)
        {
            if (string.IsNullOrWhiteSpace(fallbackPath))
            {
                continue;
            }

            var candidate = Path.GetFileName(fallbackPath).Equals(executableName, StringComparison.OrdinalIgnoreCase)
                ? fallbackPath
                : Path.Combine(fallbackPath, executableName);
            foreach (var expandedCandidate in AddPlatformExecutableExtensions(candidate))
            {
                if (candidates.Add(expandedCandidate))
                {
                    yield return expandedCandidate;
                }
            }
        }
    }

    private static IEnumerable<string> AddPlatformExecutableExtensions(string candidate)
    {
        yield return candidate;
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(candidate)))
        {
            yield return candidate + ".exe";
            yield return candidate + ".cmd";
        }
    }

    private static IReadOnlyList<string> GetPlatformFallbackPaths()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return macFallbackPaths;
        }

        return Array.Empty<string>();
    }

    private static bool HasDirectoryComponent(string path)
    {
        return Path.IsPathRooted(path)
               || path.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
               || path.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static JavaScriptRuntimeResolution Available(string executablePath)
        => new(
            executablePath,
            true,
            $"Node.js runtime available at '{executablePath}'.");

    private static JavaScriptRuntimeResolution Missing(string executablePath)
        => new(
            executablePath,
            false,
            $"Node.js runtime '{executablePath}' is unavailable. Install Node.js or configure RuntimeOptions.JavaScriptExecutablePath.");
}
