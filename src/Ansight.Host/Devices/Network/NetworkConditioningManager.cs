using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.DeviceExecution;

namespace Ansight.Host.Devices.Network;

internal sealed class NetworkConditioningManager
{
    private sealed record Lease(string Id, string Owner, NetworkConditionTarget Target,
        INetworkConditioningBackend Backend, DateTimeOffset Expires, CancellationTokenSource Stop);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Lease> leases = new(StringComparer.Ordinal);
    private readonly string root;
    private readonly Action<NetworkConditionTarget, string, JsonObject> publish;
    private readonly Func<NetworkConditionTarget, CancellationToken, Task<int>> resolve;
    private readonly Func<int, IReadOnlyList<int>, DateTimeOffset, INetworkConditioningBackend> create;

    public NetworkConditioningManager(string root, Action<NetworkConditionTarget, string, JsonObject> publish,
        Func<NetworkConditionTarget, CancellationToken, Task<int>>? resolve = null,
        Func<int, IReadOnlyList<int>, DateTimeOffset, INetworkConditioningBackend>? create = null)
    {
        this.root = root;
        this.publish = publish;
        this.resolve = resolve ?? ((target, token) => MitmNetworkBackend.ResolveProcessAsync(target, new DeviceCommandRunner(), token));
        this.create = create ?? ((pid, ports, expiry) => new MitmNetworkBackend(this.root, pid, ports, expiry));
    }

    public async Task<JsonObject> CapabilitiesAsync(NetworkConditionTarget target, CancellationToken token)
    {
        string? reason = null;
        int? pid = null;
        try
        {
            var executable = MitmNetworkBackend.FindExecutable() ?? throw new IOException("mitmdump is missing. Install mitmproxy 12.2.3 or set ANSIGHT_MITMDUMP_PATH.");
            var commands = new DeviceCommandRunner();
            var version = (await commands.RunAsync(executable, ["--version"], token).ConfigureAwait(false)).RequireText();
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"Mitmproxy:\s+12\.2\.3(?:\s|$)"))
                throw new IOException("This conditioner requires the tested mitmproxy version 12.2.3.");
            if (OperatingSystem.IsMacOS())
            {
                var extensions = (await commands.RunAsync("/usr/bin/systemextensionsctl", ["list"], token).ConfigureAwait(false)).RequireText();
                if (extensions.Split('\n').Any(line => line.Contains("org.mitmproxy.macos-redirector.network-extension", StringComparison.Ordinal) && line.Contains("waiting for user", StringComparison.Ordinal)))
                    throw new IOException("macOS is waiting for approval of mitmproxy's Network Extension in System Settings > General > Login Items & Extensions > Network Extensions.");
            }
            pid = await resolve(target, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        { reason = error.Message; }
        return new JsonObject
        {
            ["available"] = reason is null, ["reason"] = reason, ["backend"] = "mitmproxy-local-tcp",
            ["processId"] = pid, ["scope"] = target.DeviceId.StartsWith("emulator-", StringComparison.Ordinal) ? "emulator" : "app",
            ["routingVerified"] = false, ["maximumConcurrentLeases"] = 1,
            ["supported"] = JsonSerializer.SerializeToNode(new[] { "uploadKbps", "downloadKbps", "latencyMs", "jitterMs", "seed", "stall", "blocked", "scenarios" }),
            ["limitations"] = JsonSerializer.SerializeToNode(new[] {
                "macOS host; iOS Simulator or Android Emulator only. Local-capture approval may be required on first use.",
                "TCP payload pacing; UDP/QUIC, packet loss, radio state, DNS and OS link quality are not simulated.",
                "Latency/jitter delays the first payload in each direction of each connection; it is not packet-level RTT.",
                "Android scope includes all apps in the selected emulator. iOS scope is the selected app process.",
                "Ansight control ports bypass conditioning. TLS stays encrypted. Reset closes proxied connections.",
                "macOS local capture bypasses loopback destinations; use a LAN address for local test fixtures.",
                "Availability is a preflight; verify counters and application behavior after applying conditions." })
        };
    }

    public async Task<JsonObject> SetAsync(NetworkConditionTarget target, NetworkConditionProfile profile,
        IReadOnlyList<NetworkConditionStep> steps, IReadOnlyList<int> ports, int durationMs, string? leaseId, CancellationToken token)
    {
        if (durationMs is < 1000 or > 300_000) throw new ArgumentException("durationMs must be 1000–300000; leases cannot be extended.");
        if (ports.Count > 32 || ports.Any(port => port is < 1 or > 65535)) throw new ArgumentException("destinationPorts accepts at most 32 ports from 1–65535.");
        var scope = NetworkConditioningScope.Current;
        var owner = scope?.OwnerId ?? "manual";
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (leaseId is not null)
            {
                var existing = RequireLease(leaseId, owner);
                if (existing.Target.DeviceId != target.DeviceId || existing.Target.AppId != target.AppId)
                    throw new ArgumentException("The lease target cannot change.");
                if (steps.Count != 0 || ports.Count != 0) throw new ArgumentException("Updates change only profile; create a new lease to change ports or scenario.");
                await existing.Backend.ApplyAsync(profile, [], token).ConfigureAwait(false);
                var updated = Describe(existing);
                publish(existing.Target, "changed", updated);
                return updated;
            }
            if (leases.Count != 0)
                throw new InvalidOperationException("The host already has a conditioning lease. Reset it before conditioning another target; parallel conditioning is unsupported.");
            var pid = await resolve(target, token).ConfigureAwait(false);
            var expires = DateTimeOffset.UtcNow.AddMilliseconds(durationMs);
            var backend = create(pid, ports, expires);
            var lease = new Lease(Guid.NewGuid().ToString("N"), owner, target, backend, expires, new CancellationTokenSource());
            try { await backend.ApplyAsync(profile, steps, token).ConfigureAwait(false); }
            catch { await backend.DisposeAsync().ConfigureAwait(false); lease.Stop.Dispose(); throw; }
            lease = lease with { Expires = backend.ExpiresAtUtc };
            leases.Add(lease.Id, lease);
            scope?.Register(() => ResetAsync(lease.Id, owner, "task-ended"));
            var result = Describe(lease);
            _ = MonitorAsync(lease);
            publish(target, "applied", result);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<JsonObject> GetAsync(string leaseId, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return Describe(RequireLease(leaseId, NetworkConditioningScope.Current?.OwnerId ?? "manual")); }
        finally { gate.Release(); }
    }

    public Task ResetAsync(string id) => ResetAsync(id, NetworkConditioningScope.Current?.OwnerId ?? "manual", "reset");

    private async Task ResetAsync(string id, string owner, string reason)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!leases.TryGetValue(id, out var lease)) return;
            RequireLease(id, owner);
            await lease.Backend.DisposeAsync().ConfigureAwait(false);
            leases.Remove(id);
            lease.Stop.Cancel();
            publish(lease.Target, reason, new JsonObject { ["leaseId"] = id, ["status"] = "stopped", ["backend"] = lease.Backend.Name });
        }
        finally { gate.Release(); }
    }

    private async Task MonitorAsync(Lease lease)
    {
        var step = -1;
        try
        {
            while (!lease.Stop.IsCancellationRequested)
            {
                await Task.Delay(100, lease.Stop.Token).ConfigureAwait(false);
                await gate.WaitAsync(lease.Stop.Token).ConfigureAwait(false);
                try
                {
                    if (!leases.ContainsKey(lease.Id)) break;
                    var state = lease.Backend.Status();
                    if (DateTimeOffset.UtcNow >= lease.Expires || state["status"]?.GetValue<string>() == "stopped") break;
                    var current = state["stepIndex"]?.GetValue<int>() ?? 0;
                    if (current != step)
                    {
                        step = current;
                        publish(lease.Target, "step", Describe(lease));
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (lease.Stop.IsCancellationRequested) { }
        finally
        {
            await ResetAsync(lease.Id, lease.Owner, "expired-or-stopped").ConfigureAwait(false);
            lease.Stop.Dispose();
        }
    }

    public async Task StopAsync()
    {
        Lease[] active;
        await gate.WaitAsync().ConfigureAwait(false);
        try { active = leases.Values.ToArray(); }
        finally { gate.Release(); }
        foreach (var lease in active) await ResetAsync(lease.Id, lease.Owner, "host-stopped").ConfigureAwait(false);
    }

    private Lease RequireLease(string id, string owner)
    {
        if (!leases.TryGetValue(id, out var lease)) throw new ArgumentException("Conditioning lease is absent or expired.");
        if (lease.Owner != owner) throw new InvalidOperationException("This conditioning lease belongs to another task.");
        return lease;
    }

    private static JsonObject Describe(Lease lease)
    {
        var value = lease.Backend.Status();
        value["leaseId"] = lease.Id;
        value["backend"] = lease.Backend.Name;
        value["expiresAtUtc"] = lease.Expires;
        value["deviceId"] = lease.Target.DeviceId;
        value["appId"] = lease.Target.AppId;
        value["sessionId"] = lease.Target.SessionId;
        value["scope"] = lease.Target.DeviceId.StartsWith("emulator-", StringComparison.Ordinal) ? "emulator" : "app";
        return value;
    }
}
