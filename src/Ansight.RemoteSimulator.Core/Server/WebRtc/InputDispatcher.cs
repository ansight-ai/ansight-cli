using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed class InputDispatcher : IAsyncDisposable
{
    internal const int MaximumPendingMovesPerGesture = 4;
    private static readonly TimeSpan BackloggedMoveDeliveryInterval = TimeSpan.FromMilliseconds(16);

    private readonly Func<string, Task> deliverAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Lock gate = new();
    private readonly LinkedList<PendingInput> pendingInputs = [];
    private readonly SemaphoreSlim pendingSignal = new(0);
    private readonly CancellationTokenSource stopSource = new();
    private readonly Task processingTask;
    private bool disposed;

    public InputDispatcher(
        Func<string, Task> deliverAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.deliverAsync = deliverAsync ?? throw new ArgumentNullException(nameof(deliverAsync));
        this.delayAsync = delayAsync ?? Task.Delay;
        processingTask = ProcessAsync();
    }

    public void Enqueue(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var pendingInput = new PendingInput(message, ResolveMoveCoalescingKey(message));
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var last = pendingInputs.Last;
            if (pendingInput.CoalescingKey is not null
                && last?.Value.CoalescingKey is not null
                && pendingInput.CoalescingKey == last.Value.CoalescingKey
                && CountConsecutivePendingMoves(last, pendingInput.CoalescingKey)
                    >= MaximumPendingMovesPerGesture)
            {
                last.Value = pendingInput;
            }
            else
            {
                pendingInputs.AddLast(pendingInput);
                pendingSignal.Release();
            }
        }
    }

    private static int CountConsecutivePendingMoves(
        LinkedListNode<PendingInput> last,
        MoveCoalescingKey coalescingKey)
    {
        var count = 0;
        for (var node = last;
             node is not null
             && node.Value.CoalescingKey == coalescingKey
             && count < MaximumPendingMovesPerGesture;
             node = node.Previous)
        {
            count++;
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingInputs.Clear();
        }

        await stopSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
        {
        }

        stopSource.Dispose();
        pendingSignal.Dispose();
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            await pendingSignal.WaitAsync(stopSource.Token).ConfigureAwait(false);

            PendingInput? pendingInput = null;
            lock (gate)
            {
                if (pendingInputs.First is { } first)
                {
                    pendingInput = first.Value;
                    pendingInputs.RemoveFirst();
                }
            }

            if (pendingInput is not null)
            {
                await deliverAsync(pendingInput.Message).ConfigureAwait(false);
                if (pendingInput.CoalescingKey is not null && HasPendingInputs())
                {
                    // SimulatorKit consumes pointer changes on its UI run loop. When WebRTC
                    // delivers a burst, pacing the retained moves prevents the final move and
                    // up events from collapsing into one run-loop turn and losing the drag.
                    await delayAsync(BackloggedMoveDeliveryInterval, stopSource.Token).ConfigureAwait(false);
                }
            }
        }
    }

    private bool HasPendingInputs()
    {
        lock (gate)
        {
            return pendingInputs.Count > 0;
        }
    }

    private static MoveCoalescingKey? ResolveMoveCoalescingKey(string message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("phase", out var phase)
                || !string.Equals(phase.GetString(), "move", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("udid", out var deviceUdid)
                || string.IsNullOrWhiteSpace(deviceUdid.GetString())
                || !root.TryGetProperty("pointerId", out var pointerId))
            {
                return null;
            }

            var hasSecondaryContact = root.TryGetProperty("secondary", out var secondaryContact)
                                      && secondaryContact.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            return new MoveCoalescingKey(
                deviceUdid.GetString()!.Trim().ToUpperInvariant(),
                pointerId.GetRawText(),
                hasSecondaryContact);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record MoveCoalescingKey(
        string DeviceUdid,
        string PointerId,
        bool HasSecondaryContact);

    private sealed record PendingInput(string Message, MoveCoalescingKey? CoalescingKey);
}
