using System.Text.RegularExpressions;

namespace Ansight.Adb;

public static partial class ScrcpyToolLocator
{
    private static string InstallGuidance => OperatingSystem.IsMacOS()
        ? "Install it with 'brew install scrcpy', then restart the Android host."
        : OperatingSystem.IsWindows()
            ? "Reinstall the Ansight CLI to restore its bundled scrcpy, or install it with 'winget install --exact Genymobile.scrcpy', then restart the Android host."
            : "Install a complete scrcpy package with its matching scrcpy-server using your system package manager, then restart the Android host.";
    private static readonly string executableName = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";

    public static Task<ScrcpyToolResolution> ResolveAsync(
        string? configuredPath = null,
        CancellationToken cancellationToken = default)
        => ResolveAsync(configuredPath, new DotNetAdbProcessLauncher(), cancellationToken);

    public static Task<ScrcpyToolResolution> ResolveAsync(
        string? configuredPath,
        IAdbProcessLauncher processLauncher,
        CancellationToken cancellationToken = default)
        => ResolveAsync(
            EnumerateCandidates(configuredPath, AppContext.BaseDirectory, executableName, EnumeratePathDirectories()),
            processLauncher,
            Environment.GetEnvironmentVariable("SCRCPY_SERVER_PATH"),
            cancellationToken);

    internal static async Task<ScrcpyToolResolution> ResolveAsync(
        IEnumerable<ScrcpyToolCandidate> candidates,
        IAdbProcessLauncher processLauncher,
        string? configuredServer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processLauncher);
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate.Path))
            {
                continue;
            }

            var executablePath = Path.GetFullPath(candidate.Path);
            try
            {
                var bundle = candidate.Source == "bundled"
                    ? ScrcpyBundle.Validate(executablePath)
                    : null;
                var versionResult = await RunVersionAsync(
                        executablePath,
                        processLauncher,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!versionResult.IsSuccess)
                {
                    failures.Add($"'{executablePath}' could not report its version: {versionResult.Message}");
                    continue;
                }

                if (bundle is not null && versionResult.Version != bundle.Version)
                {
                    failures.Add($"Bundled scrcpy reports version {versionResult.Version}, but its server belongs to {bundle.Version}. Reinstall the Ansight CLI.");
                    continue;
                }

                // A global server override must never mix another installation's server with our bundle.
                var serverPath = bundle is not null
                    ? Path.Combine(Path.GetDirectoryName(executablePath)!, "scrcpy-server")
                    : ResolveServerPath(executablePath, configuredServer);
                if (serverPath is null)
                {
                    failures.Add($"scrcpy {versionResult.Version} at '{executablePath}' has no matching scrcpy-server file.");
                    continue;
                }

                try
                {
                    await using var stream = File.Open(serverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length == 0)
                    {
                        failures.Add($"The scrcpy server at '{serverPath}' is empty.");
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"The scrcpy server at '{serverPath}' is not readable: {ex.Message}");
                    continue;
                }

                return ScrcpyToolResolution.Found(
                    executablePath,
                    serverPath,
                    versionResult.Version,
                    candidate.Source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"scrcpy at '{executablePath}' failed validation: {ex.Message}");
            }
        }

        var detail = failures.Count == 0
            ? "scrcpy could not be found."
            : string.Join(" ", failures);
        return ScrcpyToolResolution.NotFound($"{detail} {InstallGuidance}");
    }

    public static IReadOnlyList<string> GetCandidatePaths(string? configuredPath = null)
        => EnumerateCandidates(configuredPath, AppContext.BaseDirectory, executableName, EnumeratePathDirectories())
            .Select(candidate => candidate.Path)
            .Distinct(PathComparer)
            .ToArray();

    internal static string? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output ?? string.Empty);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<ScrcpyVersionResult> RunVersionAsync(
        string executablePath,
        IAdbProcessLauncher processLauncher,
        CancellationToken cancellationToken)
    {
        var result = await new AdbClient(executablePath, processLauncher)
            .RunAsync(["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            return ScrcpyVersionResult.Failure(
                string.IsNullOrWhiteSpace(detail)
                    ? $"exit code {result.ExitCode}"
                    : detail);
        }

        var version = ParseVersion(result.StandardOutput);
        return version is null
            ? ScrcpyVersionResult.Failure("the version output was not recognized")
            : ScrcpyVersionResult.Success(version);
    }

    internal static string? ResolveServerPath(string executablePath, string? configuredServer)
    {
        if (!string.IsNullOrWhiteSpace(configuredServer) && File.Exists(configuredServer))
        {
            return Path.GetFullPath(configuredServer);
        }

        var executableInfo = new FileInfo(executablePath);
        var resolvedExecutable = executableInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? executablePath;
        var binDirectory = Path.GetDirectoryName(resolvedExecutable);
        var prefixDirectory = binDirectory is null ? null : Directory.GetParent(binDirectory)?.FullName;
        var candidates = new List<string>();
        if (binDirectory is not null)
        {
            // Windows archives and winget install the server beside the real executable.
            candidates.Add(Path.Combine(binDirectory, "scrcpy-server"));
        }
        if (prefixDirectory is not null)
        {
            candidates.Add(Path.Combine(prefixDirectory, "share", "scrcpy", "scrcpy-server"));
        }

        candidates.AddRange([
            "/opt/homebrew/share/scrcpy/scrcpy-server",
            "/usr/local/share/scrcpy/scrcpy-server",
            "/usr/share/scrcpy/scrcpy-server",
        ]);
        return candidates
            .Distinct(PathComparer)
            .FirstOrDefault(File.Exists);
    }

    internal static IEnumerable<ScrcpyToolCandidate> EnumerateCandidates(
        string? configuredPath,
        string baseDirectory,
        string executableFileName,
        IEnumerable<string> pathDirectories)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var normalized = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            yield return new ScrcpyToolCandidate(
                Directory.Exists(normalized) ? Path.Combine(normalized, executableFileName) : normalized,
                "configured");
        }

        yield return new ScrcpyToolCandidate(
            Path.Combine(baseDirectory, "tools", "scrcpy", executableFileName),
            "bundled");

        foreach (var pathDirectory in pathDirectories)
        {
            yield return new ScrcpyToolCandidate(Path.Combine(pathDirectory, executableFileName), "PATH");
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            yield return new ScrcpyToolCandidate($"/opt/homebrew/bin/{executableFileName}", "Homebrew");
            yield return new ScrcpyToolCandidate($"/usr/local/bin/{executableFileName}", "Homebrew");
        }
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

    [GeneratedRegex(@"(?im)^scrcpy\s+([0-9]+(?:\.[0-9]+){1,3}(?:[-+][A-Za-z0-9._-]+)?)\b")]
    private static partial Regex VersionPattern();

    internal sealed record ScrcpyToolCandidate(string Path, string Source);

    private sealed record ScrcpyVersionResult(bool IsSuccess, string Version, string Message)
    {
        public static ScrcpyVersionResult Success(string version) => new(true, version, string.Empty);

        public static ScrcpyVersionResult Failure(string message) => new(false, string.Empty, message);
    }
}
