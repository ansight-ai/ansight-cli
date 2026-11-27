using Ansight.Host.Devices;
using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliDeviceUsageRuntimeSource : IRefreshableRemoteRuntimeSource
{
    private readonly IRemoteRuntimeSource source;
    private readonly DeviceUsageStore usage;

    public CliDeviceUsageRuntimeSource(IRemoteRuntimeSource source, DeviceUsageStore usage)
    {
        this.source = source;
        this.usage = usage;
    }

    public RemoteRuntimeSnapshot Current
    {
        get
        {
            var snapshot = source.Current;

            return snapshot with
            {
                Devices = snapshot.Devices.Select(WithHistory)
                    .OrderByDescending(device => device.LastUsedUtc)
                    .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
    }

    public void RecordViewed(string identifier)
    {
        var device = source.Current.Devices.FirstOrDefault(device => string.Equals(device.Identifier, identifier, StringComparison.OrdinalIgnoreCase));

        if (device is not null)
        {
            RecordUse(device);
        }
    }

    public void RecordUse(RemoteRuntimeDevice device)
    {
        usage.Update(device.Platform, HistoryIdentifier(device),
            new DeviceUsageHistory(LastUsedUtc: DateTimeOffset.UtcNow));
    }

    public async Task<RemoteRuntimeSnapshot> RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        if (source is IRefreshableRemoteRuntimeSource refreshable)
        {
            await refreshable.RefreshIfStaleAsync(cancellationToken).ConfigureAwait(false);
        }

        return Current;
    }

    public async Task<RemoteRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (source is IRefreshableRemoteRuntimeSource refreshable)
        {
            await refreshable.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return Current;
    }

    private RemoteRuntimeDevice WithHistory(RemoteRuntimeDevice device)
    {
        var history = usage.Update(device.Platform, HistoryIdentifier(device),
            new DeviceUsageHistory(LastBootedUtc: device.LastBootedUtc));

        return device with { LastBootedUtc = history.LastBootedUtc, LastUsedUtc = history.LastUsedUtc };
    }

    private static string HistoryIdentifier(RemoteRuntimeDevice device)
    {
        return string.IsNullOrWhiteSpace(device.BootIdentifier) ? device.Identifier : device.BootIdentifier;
    }
}
