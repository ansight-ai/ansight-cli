using System.Diagnostics;

namespace Ansight.Host.Diagnostics;

public static class DiagnosticProcess
{
    public static IReadOnlyList<string> FindExecutables(string name, string? configured = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured) && (Path.IsPathRooted(configured) || configured.Contains(Path.DirectorySeparatorChar)))
            candidates.Add(Path.GetFullPath(configured));
        var command = !string.IsNullOrWhiteSpace(configured) && !Path.IsPathRooted(configured) && !configured.Contains(Path.DirectorySeparatorChar) ? configured : name;
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? new[] { command + ".exe", command + ".cmd", command + ".bat" } : new[] { command };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var file in names) candidates.Add(Path.Combine(directory.Trim('"'), file));
        foreach (var directory in new[] { "/opt/homebrew/bin", "/usr/local/bin", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin") })
            foreach (var file in names) candidates.Add(Path.Combine(directory, file));
        return candidates.Where(File.Exists).Select(Path.GetFullPath).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }

    public static async Task<DiagnosticProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        // Do not invoke command wrappers through a shell: paths and arguments must remain literal.
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable) is ".cmd" or ".bat")
            return new("not-checked", null, null);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment) process.StartInfo.Environment[item.Key] = item.Value;
        Task<string> stdout = Task.FromResult(string.Empty);
        Task<string> stderr = Task.FromResult(string.Empty);
        try
        {
            process.Start();
            stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode == 0 ? "available" : "failed", string.IsNullOrWhiteSpace(text) ? error : text, process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("timed-out", null, null); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return new("not-checked", null, null); }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (result.Length < 131072) result.Append(buffer, 0, Math.Min(count, 131072 - result.Length));
        return result.ToString().Trim();
    }

    public static async Task<DiagnosticTool> InspectAsync(string name, string? configured, string usedBy,
        CancellationToken token, string[]? versionArguments = null)
    {
        var paths = FindExecutables(name, configured);
        var explicitPath = !string.IsNullOrWhiteSpace(configured) && (Path.IsPathRooted(configured) || configured.Contains(Path.DirectorySeparatorChar));
        var path = explicitPath ? (File.Exists(configured) ? Path.GetFullPath(configured!) : null) : paths.FirstOrDefault();
        var via = explicitPath ? "configuration" : "discovery";
        if (path is null) return new(name, "missing", null, configured, null, via, null, null, paths, usedBy);
        var resolved = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
        var result = await RunAsync(path, versionArguments ?? ["--version"], token).ConfigureAwait(false);
        var version = result.Status == "available" ? ParseVersion(name, result.Output) : null;
        return new(name, result.Status, version, path, resolved, via, Path.GetDirectoryName(resolved),
            resolved.Contains("/Cellar/", StringComparison.Ordinal) ? "homebrew-layout" : null,
            paths.Where(candidate => candidate != path).ToArray(), usedBy);
    }

    internal static string? ParseVersion(string name, string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var lines = output.Split('\n').Select(line => line.Trim())
            .Where(line => !line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase));
        if (name == "adb") lines = lines.OrderByDescending(line => line.StartsWith("Version ", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(?<![\w.])v?(\d+\.\d+(?:\.\d+)*(?:[-+][\w.-]+)?)");
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }
}
