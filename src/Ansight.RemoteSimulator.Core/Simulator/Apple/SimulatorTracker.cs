using Ansight.SimCtl;

namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public sealed class SimulatorTracker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly SimCtlClient client;
    private readonly TimeSpan refreshInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private bool started;

    public SimulatorTracker(SimCtlClient client, TimeSpan? refreshInterval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        if (this.refreshInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        }
    }

    public event EventHandler<SimulatorTrackerSnapshot>? SnapshotChanged;

    public SimulatorTrackerSnapshot Current { get; private set; } = SimulatorTrackerSnapshot.Empty;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (started)
        {
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        started = true;
    }

    public async Task<SimulatorTrackerSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task<SimulatorTrackerSnapshot> RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Current.CapturedAtUtc != DateTimeOffset.MinValue
                && DateTimeOffset.UtcNow - Current.CapturedAtUtc < refreshInterval
                ? Current
                : await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        refreshGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<SimulatorTrackerSnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        SimulatorTrackerSnapshot next;
        try
        {
            var devices = await client.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            next = new SimulatorTrackerSnapshot(
                DateTimeOffset.UtcNow,
                devices
                    .Where(static device => device.IsAvailable)
                    .OrderByDescending(static device => device.IsBooted)
                    .ThenByDescending(static device => device.LastBootedUtc)
                    .ThenBy(static device => device.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            next = new SimulatorTrackerSnapshot(DateTimeOffset.UtcNow, [], ex.Message);
        }

        var previous = Current;
        Current = next;
        if (HasMeaningfulChange(previous, next))
        {
            SnapshotChanged?.Invoke(this, next);
        }

        return next;
    }

    private static bool HasMeaningfulChange(
        SimulatorTrackerSnapshot previous,
        SimulatorTrackerSnapshot next)
        => !string.Equals(previous.Error, next.Error, StringComparison.Ordinal)
            || !previous.Devices.SequenceEqual(next.Devices);
}
