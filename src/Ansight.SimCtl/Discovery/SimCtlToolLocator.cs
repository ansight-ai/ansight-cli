using System.Diagnostics;

namespace Ansight.SimCtl;

public static class SimCtlToolLocator
{
    private const string systemXcrunPath = "/usr/bin/xcrun";
    private const string systemXcodeSelectPath = "/usr/bin/xcode-select";

    public static async Task<SimCtlToolResolution> ResolveAsync(
        string? configuredXcodePath = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsMacCatalyst())
        {
            return SimCtlToolResolution.NotFound("SimCtl is only available on macOS.");
        }

        var candidates = new List<SimCtlToolCandidate>();
        if (!string.IsNullOrWhiteSpace(configuredXcodePath))
        {
            candidates.Add(new SimCtlToolCandidate(configuredXcodePath, "configured"));
        }

        var developerDirectory = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        if (!string.IsNullOrWhiteSpace(developerDirectory))
        {
            candidates.Add(new SimCtlToolCandidate(developerDirectory, "DEVELOPER_DIR"));
        }

        if (!OperatingSystem.IsMacCatalyst())
        {
            var selectedDirectory = await ReadSelectedDeveloperDirectoryAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectedDirectory))
            {
                candidates.Add(new SimCtlToolCandidate(selectedDirectory, "xcode-select"));
            }
        }

        candidates.Add(new SimCtlToolCandidate("/Applications/Xcode.app", "default-xcode"));
        candidates.Add(new SimCtlToolCandidate("/Applications/Xcode-beta.app", "default-xcode-beta"));
        if (Directory.Exists("/Applications"))
        {
            foreach (var xcodeApplicationPath in Directory.EnumerateDirectories("/Applications", "Xcode*.app"))
            {
                candidates.Add(new SimCtlToolCandidate(xcodeApplicationPath, "applications"));
            }
        }

        foreach (var candidate in candidates.DistinctBy(
                     value => NormalizeDeveloperDirectory(value.Path),
                     StringComparer.Ordinal))
        {
            var normalizedDeveloperDirectory = NormalizeDeveloperDirectory(candidate.Path);
            var simCtlPath = Path.Combine(normalizedDeveloperDirectory, "usr", "bin", "simctl");
            if (File.Exists(systemXcrunPath) && File.Exists(simCtlPath))
            {
                return SimCtlToolResolution.Found(
                    Path.GetFullPath(normalizedDeveloperDirectory),
                    systemXcrunPath,
                    Path.GetFullPath(simCtlPath),
                    candidate.Source);
            }
        }

        return SimCtlToolResolution.NotFound(
            "SimCtl could not be found. Install Xcode or set ANSIGHT_XCODE_PATH to the Xcode application path.");
    }

    public static string NormalizeDeveloperDirectory(string path)
    {
        var normalized = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
        if (normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(normalized, "Contents", "Developer");
        }

        if (normalized.EndsWith(Path.Combine("Contents", "Developer", "usr", "bin", "simctl"), StringComparison.Ordinal))
        {
            return Directory.GetParent(normalized)?.Parent?.Parent?.FullName ?? normalized;
        }

        return normalized;
    }

    private static async Task<string?> ReadSelectedDeveloperDirectoryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(systemXcodeSelectPath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = systemXcodeSelectPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-p");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return null;
        }

        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // This exception is an expected fallback for the best-effort operation.
        }
    }

    private sealed record SimCtlToolCandidate(string Path, string Source);
}
