using Ansight.SimCtl;

namespace Ansight.RemoteSimulator.Core.Simulator.Apple;

public sealed class SimulatorTracker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private readonly SimCtlClient client;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private CancellationTokenSource? pollingCancellation;
    private Task? pollingTask;

    public SimulatorTracker(SimCtlClient client, TimeSpan? pollInterval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public event EventHandler<SimulatorTrackerSnapshot>? SnapshotChanged;

    public SimulatorTrackerSnapshot Current { get; private set; } = SimulatorTrackerSnapshot.Empty;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (pollingTask is not null)
        {
            return;
        }

        pollingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await RefreshAsync(pollingCancellation.Token).ConfigureAwait(false);
        pollingTask = PollAsync(pollingCancellation.Token);
    }

    public async Task<SimulatorTrackerSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                next = new SimulatorTrackerSnapshot(DateTimeOffset.UtcNow, Current.Devices, ex.Message);
            }

            var previous = Current;
            Current = next;
            if (!HasMeaningfulChange(previous, next))
            {
                return next;
            }

            SnapshotChanged?.Invoke(this, next);
            return next;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pollingCancellation is not null)
        {
            await pollingCancellation.CancelAsync().ConfigureAwait(false);
        }

        if (pollingTask is not null)
        {
            try
            {
                await pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected completion path.
            }
        }

        pollingCancellation?.Dispose();
        refreshGate.Dispose();
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasMeaningfulChange(
        SimulatorTrackerSnapshot previous,
        SimulatorTrackerSnapshot next)
        => !string.Equals(previous.Error, next.Error, StringComparison.Ordinal)
            || !previous.Devices.SequenceEqual(next.Devices);
}
