using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ansight.RemoteSimulator.Core.Simulator.Android.Grpc.Audio;

public sealed partial class AndroidAudioEndpointResolver
{
    private readonly IReadOnlyList<string> directories;
    private readonly Func<int, long?> processStartReader;

    public AndroidAudioEndpointResolver() : this(GetDiscoveryDirectories(), GetEmulatorProcessStartTicks) { }

    internal AndroidAudioEndpointResolver(IReadOnlyList<string> directories, Func<int, long?> processStartReader)
    {
        this.directories = directories;
        this.processStartReader = processStartReader;
    }

    public AndroidAudioEndpoint Resolve(string serial)
    {
        ArgumentNullException.ThrowIfNull(serial);
        var match = SerialPattern().Match(serial);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var consolePort)
            || consolePort is < 1024 or > 65534 || consolePort % 2 != 0)
        {
            throw new AndroidAudioEndpointException("unsupported-device", "Audio injection requires a local Android emulator serial.");
        }

        var candidates = new List<AndroidAudioEndpoint>();
        var unauthenticated = false;
        foreach (var directory in directories.Distinct(StringComparer.Ordinal))
        {
            string[] paths;
            try { paths = Directory.GetFiles(directory, "pid_*.ini"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

            foreach (var path in paths)
            {
                var processMatch = ProcessFilePattern().Match(Path.GetFileName(path));
                if (!processMatch.Success || !int.TryParse(processMatch.Groups[1].Value, out var processId))
                {
                    continue;
                }
                var processStart = processStartReader(processId);
                if (processStart is null) { continue; }

                Dictionary<string, string> entries;
                try
                {
                    if (new FileInfo(path).Length > 65536) { continue; }
                    entries = new(StringComparer.Ordinal);
                    foreach (var line in File.ReadLines(path))
                    {
                        var separator = line.IndexOf('=');
                        if (separator > 0) { entries[line[..separator].Trim()] = line[(separator + 1)..].Trim(); }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

                var isTarget = entries.TryGetValue("port.serial", out var serialValue)
                    ? serialValue == serial || serialValue == consolePort.ToString(CultureInfo.InvariantCulture)
                    : entries.GetValueOrDefault("port.adb") == (consolePort + 1).ToString(CultureInfo.InvariantCulture);
                if (!isTarget || !int.TryParse(entries.GetValueOrDefault("grpc.port"), out var port) || port is < 1 or > 65535)
                {
                    continue;
                }
                var token = entries.GetValueOrDefault("grpc.token");
                if (string.IsNullOrWhiteSpace(token) || token.Any(character => character is < '!' or > '~'))
                {
                    unauthenticated = true;
                    continue;
                }
                var endpoint = new AndroidAudioEndpoint(serial, processId, processStart.Value, port, token);
                if (!candidates.Any(existing => existing.Matches(endpoint))) { candidates.Add(endpoint); }
            }
        }
        if (candidates.Count > 1 || (candidates.Count > 0 && unauthenticated))
        {
            throw new AndroidAudioEndpointException("endpoint-ambiguous", "Multiple live authenticated emulator endpoints match this device; no audio was injected.");
        }
        if (candidates.Count == 0)
        {
            throw new AndroidAudioEndpointException(unauthenticated ? "endpoint-authentication-unavailable" : "endpoint-unavailable",
                unauthenticated ? "The emulator discovery record has no usable bearer token. An authenticated gRPC endpoint is required."
                    : "No live local gRPC discovery record matches this emulator. Configure its authenticated gRPC endpoint before injecting audio.");
        }
        return candidates[0];
    }

    public bool IsCurrent(AndroidAudioEndpoint endpoint)
    {
        try { return Resolve(endpoint.Serial).Matches(endpoint); }
        catch (AndroidAudioEndpointException) { return false; }
    }

    private static long? GetEmulatorProcessStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || !(process.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("emulator", StringComparison.OrdinalIgnoreCase))) { return null; }
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> GetDiscoveryDirectories()
    {
        var result = new List<string> { Path.Combine(Path.GetTempPath(), "avd", "running") };
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        result.Add(Path.Combine(userProfile, ".android", "avd", "running"));
        if (OperatingSystem.IsMacOS()) { result.Add(Path.Combine(userProfile, "Library", "Caches", "TemporaryItems", "avd", "running")); }
        foreach (var variable in new[] { "TMPDIR", "XDG_RUNTIME_DIR" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value) { result.Add(Path.Combine(value, "avd", "running")); }
        }
        return result;
    }

    [GeneratedRegex("^emulator-([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SerialPattern();
    [GeneratedRegex("^pid_([0-9]+)\\.ini$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessFilePattern();
}
