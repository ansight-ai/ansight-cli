using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ansight.Host.Runtime.DeviceExecution;

namespace Ansight.Host.Devices.Network;

internal interface INetworkConditioningBackend : IAsyncDisposable
{
    string Name { get; }
    DateTimeOffset ExpiresAtUtc { get; }
    Task ApplyAsync(NetworkConditionProfile profile, IReadOnlyList<NetworkConditionStep> steps, CancellationToken cancellationToken);
    JsonObject Status();
}

internal sealed class MitmNetworkBackend : INetworkConditioningBackend
{
    private readonly string directory;
    private readonly string configPath;
    private readonly int processId;
    private readonly DateTimeOffset processStart;
    private readonly IReadOnlyList<int> ports;
    private DateTimeOffset expires;
    private readonly TimeSpan lifetime;
    private readonly object errorGate = new();
    private Process? proxy;
    private int revision;
    private string error = "";
    public DateTimeOffset ExpiresAtUtc => expires;
    public string Name => "mitmproxy-local-tcp";

    public MitmNetworkBackend(string root, int processId, IReadOnlyList<int> ports, DateTimeOffset expires)
    {
        directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        configPath = Path.Combine(directory, "conditions.json");
        this.processId = processId;
        using var app = Process.GetProcessById(processId);
        processStart = app.StartTime;
        this.ports = ports;
        this.expires = expires;
        lifetime = expires - DateTimeOffset.UtcNow;
    }

    internal static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("ANSIGHT_MITMDUMP_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.IsPathRooted(configured) && File.Exists(configured) ? configured : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin"])
            .Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "mitmdump.exe" : "mitmdump"))
            .FirstOrDefault(File.Exists);
    }

    internal static async Task<int> ResolveProcessAsync(NetworkConditionTarget target, IDeviceCommandRunner commands, CancellationToken token)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Local conditioning currently requires a macOS host.");
        if (Regex.IsMatch(target.DeviceId, @"^emulator-[0-9]{4,5}$"))
        {
            var port = int.Parse(target.DeviceId[9..], System.Globalization.CultureInfo.InvariantCulture);
            if (port is < 5554 or > 5682 || port % 2 != 0) throw new ArgumentException("Invalid emulator console port.");
            var listening = await commands.RunAsync("/usr/sbin/lsof", ["-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-t"], token).ConfigureAwait(false);
            var pids = listening.RequireText().Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            if (pids.Length != 1 || !int.TryParse(pids[0], out var emulatorPid)) throw new InvalidOperationException("The emulator console process must resolve uniquely.");
            using var emulator = Process.GetProcessById(emulatorPid);
            if (!emulator.ProcessName.StartsWith("qemu-system-", StringComparison.Ordinal)) throw new InvalidOperationException("The selected console listener is not an Android emulator process.");
            return emulatorPid;
        }
        if (!Guid.TryParse(target.DeviceId, out _)
            || !Regex.IsMatch(target.AppId, @"^[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+$"))
            throw new PlatformNotSupportedException("mitmproxy local conditioning requires an exact iOS Simulator UDID and app bundle identifier on macOS.");
        var result = await commands.RunAsync("/usr/bin/xcrun", ["simctl", "spawn", target.DeviceId, "launchctl", "list"], token).ConfigureAwait(false);
        return ParseProcessId(result.RequireText(), target.AppId);
    }

    internal static int ParseProcessId(string services, string appId)
    {
        var matches = services.Split('\n').Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 3 && parts[2].StartsWith("UIKitApplication:" + appId + "[", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || !int.TryParse(matches[0][0], out var pid) || pid <= 0)
            throw new InvalidOperationException("Launch the target app first; its simulator process must resolve uniquely.");
        return pid;
    }

    public async Task ApplyAsync(NetworkConditionProfile profile, IReadOnlyList<NetworkConditionStep> steps, CancellationToken cancellationToken)
    {
        using var app = Process.GetProcessById(processId);
        if (app.HasExited || app.StartTime != processStart)
            throw new InvalidOperationException("The target app restarted. Reset conditioning and start a new lease for its new process.");
        if (proxy is null)
        {
            // Establish local routing with a neutral profile before starting the
            // requested lifetime. Initial OS approval must not consume scenario steps.
            expires = DateTimeOffset.UtcNow.AddSeconds(20);
            await WriteConfigAsync(new NetworkConditionProfile(), [], cancellationToken).ConfigureAwait(false);
            await StartProxyAsync(cancellationToken).ConfigureAwait(false);
            await WaitReadyAsync(cancellationToken).ConfigureAwait(false);
            expires = DateTimeOffset.UtcNow.Add(lifetime);
        }
        await WriteConfigAsync(profile, steps, cancellationToken).ConfigureAwait(false);
        await WaitReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteConfigAsync(NetworkConditionProfile profile, IReadOnlyList<NetworkConditionStep> steps, CancellationToken cancellationToken)
    {
        var value = new
        {
            revision = ++revision, parentPid = Environment.ProcessId, targetPid = processId,
            targetStartedAtUnixSeconds = processStart.ToUnixTimeSeconds(),
            expiresAtUnixSeconds = expires.ToUnixTimeMilliseconds() / 1000d,
            startedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            profile, steps, destinationPorts = ports,
            bypassPorts = Enumerable.Range(ProtocolDefaults.WebSocketSessionPortRangeStart,
                ProtocolDefaults.WebSocketSessionPortRangeEnd - ProtocolDefaults.WebSocketSessionPortRangeStart + 1)
                .Append(ProtocolDefaults.WebSocketPort).Append(ProtocolDefaults.DiscoveryPort).Distinct().ToArray()
        };
        var temporary = configPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, NetworkConditionProfile.JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, configPath, true);
    }

    private async Task StartProxyAsync(CancellationToken cancellationToken)
    {
        if (proxy is null)
        {
            var addonPath = Path.Combine(directory, "conditioner.py");
            await File.WriteAllTextAsync(addonPath, EmbeddedTextResource.Read("Devices/Network/Resources/conditioner.py"), cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(FindExecutable() ?? throw new IOException("Install mitmproxy: brew install --cask mitmproxy"))
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { "--mode", $"local:{processId}", "--ignore-hosts", ".*", "--set", "show_ignored_hosts=true", "--set", "flow_detail=0", "--set", "termlog_verbosity=warn", "--set", $"confdir={directory}", "-s", addonPath })
                start.ArgumentList.Add(argument);
            start.Environment["ANSIGHT_NETWORK_CONFIG"] = configPath;
            proxy = Process.Start(start) ?? throw new IOException("mitmdump failed to start.");
            proxy.OutputDataReceived += (_, args) => RememberError(args.Data);
            proxy.ErrorDataReceived += (_, args) => RememberError(args.Data);
            proxy.BeginOutputReadLine();
            proxy.BeginErrorReadLine();
        }
    }

    private async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (proxy!.HasExited) throw new IOException("mitmdump could not establish local capture. " + error);
                var state = Status();
                if (state["revision"]?.GetValue<int>() == revision && state["status"]?.GetValue<string>() == "ready") return;
                await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("mitmdump local capture did not become ready. macOS may require approving its network extension. " + error);
        }
    }

    private void RememberError(string? value)
    {
        if (value is not null) lock (errorGate) error = (error + " " + value)[^Math.Min(4096, error.Length + value.Length + 1)..];
    }

    public JsonObject Status()
    {
        try
        {
            var value = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(configPath, ".status.json")))!.AsObject();
            value["processId"] = processId;
            if (proxy?.HasExited == true) value["status"] = "stopped";
            return value;
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return new JsonObject { ["status"] = "starting" }; }
    }

    public async ValueTask DisposeAsync()
    {
        if (proxy is not null)
        {
            try
            {
                if (!proxy.HasExited) proxy.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { } // Independent watchdog may have exited first.
            await proxy.WaitForExitAsync().ConfigureAwait(false);
            proxy.Dispose();
            proxy = null;
        }
        // This directory contains only this lease's addon/config and unused generated CA.
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { } // Routing is already gone; leftover private temporary files cannot keep it active.
        catch (UnauthorizedAccessException) { }
    }
}
