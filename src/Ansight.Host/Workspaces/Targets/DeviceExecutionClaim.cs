using System.Collections.Concurrent;

namespace Ansight.Host.Workspaces.Targets;

internal sealed class DeviceExecutionClaim : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> claimedDevices = new(StringComparer.OrdinalIgnoreCase);
    private string? key;

    private DeviceExecutionClaim(string key) => this.key = key;

    public static DeviceExecutionClaim Acquire(DeviceDescriptor device)
        => TryAcquire(device) ?? throw new InvalidOperationException(
            $"Device '{device.Name}' already has an active device execution or app watch. Stop the execution or disable its watch first.");

    internal static DeviceExecutionClaim? TryAcquire(DeviceDescriptor device)
    {
        // Android inventory reports both an AVD name and a serial as it boots.
        var key = $"{device.Platform}:{(device.Platform == DevicePlatforms.Android ? device.Name : device.Identifier)}";
        if (!claimedDevices.TryAdd(key, 0)) return null;
        return new DeviceExecutionClaim(key);
    }

    public void Dispose()
    {
        var released = Interlocked.Exchange(ref key, null);
        if (released is not null) claimedDevices.TryRemove(released, out _);
    }
}
