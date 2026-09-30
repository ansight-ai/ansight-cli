using Ansight.MacSimulatorHid;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Simulator.Apple;

namespace Ansight.Cli.LocalSimulator;

internal sealed class CliSimulatorHidInputSink : ISimulatorInputSink
{
    private readonly MacSimulatorHidSession session;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Lock statusGate = new();
    private string status = "SimulatorKit HID input is ready.";
    private bool disposed;

    public CliSimulatorHidInputSink(string developerDirectory)
    {
        session = new MacSimulatorHidSession(developerDirectory);
    }

    public string BackendName => "simulator-kit-hid-cli";

    public string Status
    {
        get
        {
            lock (statusGate)
            {
                return status;
            }
        }
    }

    public Task<InputDeliveryResult> SendAsync(
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken = default)
        => SendLockedAsync(
            () =>
            {
                var normalized = pointerEvent.NormalizeAndValidate();
                session.SendPointer(
                    normalized.DeviceUdid,
                    MapPhase(normalized.Phase),
                    normalized.NormalizedX,
                    normalized.NormalizedY,
                    normalized.SecondaryContact is null
                        ? null
                        : new MacSimulatorTouchContact(
                            normalized.SecondaryContact.NormalizedX,
                            normalized.SecondaryContact.NormalizedY),
                    normalized.PointerId,
                    normalized.TimestampMilliseconds);
            },
            "Native SimulatorKit touch input delivered.",
            cancellationToken);

    public Task<InputDeliveryResult> SendButtonAsync(
        RemoteButtonEvent buttonEvent,
        CancellationToken cancellationToken = default)
        => SendLockedAsync(
            () =>
            {
                var normalized = buttonEvent.NormalizeAndValidate();
                var button = MapButton(normalized.Button);
                session.SendButton(normalized.DeviceUdid, button, MacSimulatorButtonPhase.Down);
                session.SendButton(normalized.DeviceUdid, button, MacSimulatorButtonPhase.Up);
            },
            "Native SimulatorKit button input delivered.",
            cancellationToken);

    public Task<InputDeliveryResult> SendKeyAsync(
        RemoteKeyEvent keyEvent,
        CancellationToken cancellationToken = default)
        => SendLockedAsync(
            () =>
            {
                var normalized = keyEvent.NormalizeAndValidate();
                session.SendKey(
                    normalized.DeviceUdid,
                    normalized.UsageCode,
                    MapKeyPhase(normalized.Phase));
            },
            "Native SimulatorKit keyboard input delivered.",
            cancellationToken);

    public Task<InputDeliveryResult> SendTextAsync(
        RemoteTextEvent textEvent,
        CancellationToken cancellationToken = default)
        => SendLockedAsync(
            () =>
            {
                var normalized = textEvent.NormalizeAndValidate();
                foreach (var stroke in SimulatorTextInputMapper.Map(normalized.Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stroke.RequiresShift)
                    {
                        session.SendKey(normalized.DeviceUdid, 225, MacSimulatorKeyPhase.Down);
                    }

                    session.SendKey(normalized.DeviceUdid, stroke.UsageCode, MacSimulatorKeyPhase.Down);
                    session.SendKey(normalized.DeviceUdid, stroke.UsageCode, MacSimulatorKeyPhase.Up);
                    if (stroke.RequiresShift)
                    {
                        session.SendKey(normalized.DeviceUdid, 225, MacSimulatorKeyPhase.Up);
                    }
                }
            },
            "Native SimulatorKit text input delivered.",
            cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        session.Dispose();
        sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<InputDeliveryResult> SendLockedAsync(
        Action send,
        string successStatus,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            send();
            SetStatus(successStatus);
            return InputDeliveryResult.Success(BackendName, Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus(exception.Message);
            return InputDeliveryResult.Failure(BackendName, exception.Message);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static MacSimulatorPointerPhase MapPhase(string phase)
        => phase switch
        {
            "down" => MacSimulatorPointerPhase.Down,
            "move" => MacSimulatorPointerPhase.Move,
            "up" => MacSimulatorPointerPhase.Up,
            "cancel" => MacSimulatorPointerPhase.Cancel,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported pointer phase.")
        };

    private static MacSimulatorButton MapButton(string button)
        => button switch
        {
            "home" => MacSimulatorButton.Home,
            "lock" => MacSimulatorButton.Lock,
            "volume-up" => MacSimulatorButton.VolumeUp,
            "volume-down" => MacSimulatorButton.VolumeDown,
            _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unsupported simulator button.")
        };

    private static MacSimulatorKeyPhase MapKeyPhase(string phase)
        => phase switch
        {
            "down" => MacSimulatorKeyPhase.Down,
            "up" => MacSimulatorKeyPhase.Up,
            _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unsupported keyboard phase.")
        };

    private void SetStatus(string value)
    {
        lock (statusGate)
        {
            status = value;
        }
    }
}
