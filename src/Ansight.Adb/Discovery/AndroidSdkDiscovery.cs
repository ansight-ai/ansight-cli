namespace Ansight.Adb;

/// <summary>Finds existing SDK roots without scanning disks or changing installations.</summary>
internal static class AndroidSdkDiscovery
{
    internal static IEnumerable<string> FromPath(string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(searchPath)) yield break;
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var managerSuffix = OperatingSystem.IsWindows() ? ".bat" : string.Empty;
        var names = new[] { "adb" + suffix, "emulator" + suffix, "sdkmanager" + managerSuffix, "avdmanager" + managerSuffix };
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var root = FromTool(Path.Combine(directory.Trim('"'), name));
                if (root is not null && seen.Add(root)) yield return root;
            }
        }
    }

    internal static string? FromTool(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var resolvedPath = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
            var directory = Directory.GetParent(resolvedPath);
            // Covers platform-tools, emulator, tools/bin, and cmdline-tools/<version>/bin.
            for (var depth = 0; depth < 4 && directory is not null; depth++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "platform-tools"))
                    || Directory.Exists(Path.Combine(directory.FullName, "emulator"))
                    || Directory.Exists(Path.Combine(directory.FullName, "cmdline-tools")))
                    return directory.FullName;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An inaccessible or dangling PATH entry must not hide another usable SDK.
        }
        return null;
    }

    internal static IEnumerable<string> SystemRoots()
    {
        if (!OperatingSystem.IsLinux()) yield break;
        yield return "/usr/lib/android-sdk";
        yield return "/usr/local/lib/android/sdk";
        yield return "/opt/android-sdk";
        yield return "/opt/android/sdk";
    }
}
