using Ansight.RemoteSimulator.Core.Runtime;

namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public sealed class SimulatorRuntimeSource : IRefreshableRemoteRuntimeSource
{
    private readonly SimulatorTracker tracker;

    public SimulatorRuntimeSource(SimulatorTracker tracker)
    {
        this.tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
    }

    public RemoteRuntimeSnapshot Current
    {
        get
        {
            var snapshot = tracker.Current;
            return new RemoteRuntimeSnapshot(
                snapshot.CapturedAtUtc,
                snapshot.Devices
                    .Select(static device => new RemoteRuntimeDevice(
                        device.Udid,
                        device.Name,
                        device.State,
                        device.IsBooted,
                        device.RuntimeIdentifier,
                        "ios",
                        LastBootedUtc: device.LastBootedUtc,
                        BootIdentifier: device.Udid))
                    .ToArray(),
                snapshot.Error);
        }
    }

    public async Task<RemoteRuntimeSnapshot> RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        await tracker.RefreshIfStaleAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }

    public async Task<RemoteRuntimeSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await tracker.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }
}
