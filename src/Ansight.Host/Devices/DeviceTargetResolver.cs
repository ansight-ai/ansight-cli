using Ansight.Adb;

namespace Ansight.Host.Devices;

internal sealed class DeviceTargetResolver(
    Func<CancellationToken, Task<DeviceInventory>> listDevices,
    Func<CancellationToken, Task<IReadOnlyList<AdbDevice>>> listAndroidDevices,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan cacheDuration = TimeSpan.FromSeconds(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DeviceDescriptor?> ResolveAsync(string identifier, CancellationToken cancellationToken)
    {
        var key = identifier?.Trim() ?? string.Empty;
        if (key.Length == 0) return null;

        CacheEntry? cached;
        lock (gate)
        {
            cache.TryGetValue(key, out cached);
            if (cached is not null && clock.GetUtcNow() - cached.CapturedAtUtc <= cacheDuration)
                return cached.Device;
        }

        DeviceDescriptor? resolved;
        var knownAndroid = cached?.Device.Platform == DevicePlatforms.Android;
        if (knownAndroid)
        {
            // UIAutomator often outlasts the cache. Recheck the known ADB serial without
            // repeatedly enumerating AVDs, iOS simulators, and physical Apple devices.
            var connected = await listAndroidDevices(cancellationToken).ConfigureAwait(false);
            var device = connected.FirstOrDefault(candidate => string.Equals(
                candidate.Serial, cached!.Device.Identifier, StringComparison.OrdinalIgnoreCase));
            if (device is null && cached!.Device.IsVirtual
                && !string.Equals(key, cached.Device.Identifier, StringComparison.OrdinalIgnoreCase))
            {
                // A named AVD may restart on a different runtime serial.
                resolved = FindTarget(await listDevices(cancellationToken).ConfigureAwait(false), key);
            }
            else
            {
                resolved = cached!.Device with
                {
                    State = device?.State ?? "disconnected",
                    IsBooted = device?.IsConnected == true,
                    IsAvailable = device?.IsConnected == true
                };
            }
        }
        else
        {
            var inventory = await listDevices(cancellationToken).ConfigureAwait(false);
            resolved = FindTarget(inventory, key);
        }

        lock (gate)
        {
            // Retain a known serial through disconnect/reconnect, but do not cache an
            // unstarted AVD name: its runtime serial is not known until it boots.
            if (resolved is not null && (resolved.IsBooted || knownAndroid))
                cache[key] = new CacheEntry(clock.GetUtcNow(), resolved);
            else
                cache.Remove(key);
        }
        return resolved;
    }

    private static DeviceDescriptor? FindTarget(DeviceInventory inventory, string key)
        => inventory.Devices.FirstOrDefault(device => string.Equals(
            device.Identifier, key, StringComparison.OrdinalIgnoreCase))
            ?? inventory.Devices.FirstOrDefault(device => device.IsBooted
                && device.Platform == DevicePlatforms.Android && device.IsVirtual
                && string.Equals(device.Name, key, StringComparison.OrdinalIgnoreCase));

    private sealed record CacheEntry(DateTimeOffset CapturedAtUtc, DeviceDescriptor Device);
}
