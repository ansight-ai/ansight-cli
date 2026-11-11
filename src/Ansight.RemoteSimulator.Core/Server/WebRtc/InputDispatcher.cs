using System.Text.Json;

namespace Ansight.RemoteSimulator.Core.Server.WebRtc;

internal sealed class InputDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan backloggedMoveDeliveryInterval = TimeSpan.FromMilliseconds(16);

    private const int MaximumMessageCharacters = 64 * 1024;
    private const int MaximumPendingMessages = 128;
    private const int MaximumPendingCharacters = 512 * 1024;
    private readonly Func<string, CancellationToken, Task> deliverAsync;
    private readonly Action? onOverflow;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly Lock gate = new();
    private readonly LinkedList<PendingInput> pendingInputs = [];
    private readonly SemaphoreSlim pendingSignal = new(0);
    private readonly CancellationTokenSource stopSource = new();
    private readonly Task processingTask;
    private bool disposed;
    private bool overloaded;
    private int pendingCharacters;

    public InputDispatcher(
        Func<string, Task> deliverAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
        : this((message, _) => deliverAsync(message), delayAsync)
    {
    }

    public InputDispatcher(
        Func<string, CancellationToken, Task> deliverAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action? onOverflow = null)
    {
        this.deliverAsync = deliverAsync ?? throw new ArgumentNullException(nameof(deliverAsync));
        this.delayAsync = delayAsync ?? Task.Delay;
        this.onOverflow = onOverflow;
        processingTask = ProcessAsync();
    }

    public void Enqueue(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (gate)
        {
            if (disposed || overloaded)
            {
                return;
            }

            // Check size before parsing or retaining attacker-controlled JSON.
            if (message.Length > MaximumMessageCharacters)
            {
                RejectOverflow();

                return;
            }

            var pendingInput = new PendingInput(message, ResolveMoveCoalescingKey(message));
            var last = pendingInputs.Last;
            var replacesPendingMove = pendingInput.CoalescingKey is not null
                && last?.Value.CoalescingKey is not null
                && pendingInput.CoalescingKey == last.Value.CoalescingKey;
            var nextCharacters = pendingCharacters + message.Length
                - (replacesPendingMove ? last!.Value.Message.Length : 0);
            var exceedsCapacity = nextCharacters > MaximumPendingCharacters
                || (!replacesPendingMove && pendingInputs.Count >= MaximumPendingMessages);

            if (exceedsCapacity)
            {
                RejectOverflow();

                return;
            }

            pendingCharacters = nextCharacters;

            if (replacesPendingMove)
            {
                // A live drag should follow the current finger position. Replaying old
                // positions after a slow delivery makes every following input feel late.
                last!.Value = pendingInput;
            }
            else
            {
                pendingInputs.AddLast(pendingInput);
                pendingSignal.Release();
            }
        }
    }

    private void RejectOverflow()
    {
        overloaded = true;
        pendingInputs.Clear();
        pendingCharacters = 0;
        onOverflow?.Invoke();
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
            pendingCharacters = 0;
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
                    pendingCharacters -= pendingInput.Message.Length;
                }
            }

            if (pendingInput is not null)
            {
                await deliverAsync(pendingInput.Message, stopSource.Token)
                    .WaitAsync(stopSource.Token).ConfigureAwait(false);

                if (pendingInput.CoalescingKey is not null && HasPendingInputs())
                {
                    // SimulatorKit consumes pointer changes on its UI run loop. When WebRTC
                    // delivers a burst, pacing the retained moves prevents the final move and
                    // up events from collapsing into one run-loop turn and losing the drag.
                    await delayAsync(backloggedMoveDeliveryInterval, stopSource.Token).ConfigureAwait(false);
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

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var isMove = root.TryGetProperty("phase", out var phase)
                && string.Equals(phase.GetString(), "move", StringComparison.OrdinalIgnoreCase);

            if (!isMove || !root.TryGetProperty("udid", out var deviceUdid))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(deviceUdid.GetString())
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
