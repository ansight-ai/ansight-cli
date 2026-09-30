using System.Globalization;
using Ansight.Adb;
using Ansight.RemoteSimulator.Core.Input;
using Ansight.RemoteSimulator.Core.Runtime;
using Ansight.RemoteSimulator.Core.Simulator.Android.Input;

namespace Ansight.RemoteSimulator.Core.Simulator.Android;

public sealed class AndroidEmulatorInputSink : ISimulatorInputSink
{
    private const double TapDistanceThreshold = 0.0125d;
    private const int ActiveTouchPressure = 1024;
    private readonly IAndroidEmulatorClient client;
    private readonly Func<string, RemoteRuntimeDevice?> deviceResolver;
    private readonly IAndroidEmulatorController? controller;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Lock stateGate = new();
    private readonly Dictionary<AndroidPointerKey, AndroidPointerGesture> gestures = [];
    private readonly Dictionary<string, HashSet<uint>> pressedModifiers = new(StringComparer.OrdinalIgnoreCase);
    private string status = "Android Emulator live input is ready with ADB fallback.";
    private bool disposed;

    public AndroidEmulatorInputSink(
        IAndroidEmulatorClient client,
        Func<string, RemoteRuntimeDevice?> deviceResolver,
        IAndroidEmulatorController? controller = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.deviceResolver = deviceResolver ?? throw new ArgumentNullException(nameof(deviceResolver));
        this.controller = controller;
    }

    public string BackendName => "android-emulator-grpc+adb";

    public string Status
    {
        get
        {
            lock (stateGate)
            {
                return status;
            }
        }
    }

    public async Task<InputDeliveryResult> SendAsync(
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = pointerEvent.NormalizeAndValidate();
        var device = ResolveReadyDevice(normalized.DeviceUdid);
        if (device is null)
        {
            return Failure("The Android emulator display size is unavailable.");
        }

        if (controller is not null)
        {
            try
            {
                return await DeliverLiveTouchAsync(device, normalized, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (normalized.SecondaryContact is not null)
                {
                    return Failure($"Android Emulator multi-touch input failed: {ex.Message}");
                }
            }
        }

        if (normalized.SecondaryContact is not null)
        {
            return Failure("Android Emulator multi-touch input is unavailable; the gRPC controller could not be reached.");
        }

        var key = new AndroidPointerKey(normalized.DeviceUdid, normalized.PointerId);
        switch (normalized.Phase)
        {
            case "down":
                lock (stateGate)
                {
                    gestures[key] = AndroidPointerGesture.Start(normalized);
                }
                return Success("ADB gesture capture started.");
            case "move":
                lock (stateGate)
                {
                    if (!gestures.TryGetValue(key, out var gesture))
                    {
                        return FailureLocked("The Android gesture did not begin with a pointer-down event.");
                    }

                    gestures[key] = gesture.Update(normalized);
                }
                return Success("ADB gesture capture updated.");
            case "cancel":
                lock (stateGate)
                {
                    gestures.Remove(key);
                }
                return Success("ADB gesture capture cancelled.");
            case "up":
                AndroidPointerGesture completedGesture;
                lock (stateGate)
                {
                    if (!gestures.Remove(key, out var gesture))
                    {
                        return FailureLocked("The Android gesture did not begin with a pointer-down event.");
                    }

                    completedGesture = gesture.Update(normalized);
                }

                return await DeliverGestureAsync(device, completedGesture, cancellationToken).ConfigureAwait(false);
            default:
                return Failure("The Android pointer phase is unsupported.");
        }
    }

    public Task<InputDeliveryResult> SendButtonAsync(
        RemoteButtonEvent buttonEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = buttonEvent.NormalizeAndValidate();
        var keyCode = normalized.Button switch
        {
            "home" => "KEYCODE_HOME",
            "back" => "KEYCODE_BACK",
            "lock" => "KEYCODE_POWER",
            "volume-up" => "KEYCODE_VOLUME_UP",
            "volume-down" => "KEYCODE_VOLUME_DOWN",
            _ => throw new ArgumentOutOfRangeException(nameof(buttonEvent), normalized.Button, "Unsupported Android button."),
        };
        return RunInputAsync(
            normalized.DeviceUdid,
            ["keyevent", keyCode],
            $"ADB {normalized.Button} button input delivered.",
            cancellationToken);
    }

    public Task<InputDeliveryResult> SendKeyAsync(
        RemoteKeyEvent keyEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = keyEvent.NormalizeAndValidate();
        if (HidKeyMapper.IsModifier(normalized.UsageCode))
        {
            lock (stateGate)
            {
                if (!pressedModifiers.TryGetValue(normalized.DeviceUdid, out var modifierSet))
                {
                    modifierSet = [];
                    pressedModifiers[normalized.DeviceUdid] = modifierSet;
                }

                if (normalized.Phase == "down")
                {
                    modifierSet.Add(normalized.UsageCode);
                }
                else
                {
                    modifierSet.Remove(normalized.UsageCode);
                    if (modifierSet.Count == 0)
                    {
                        pressedModifiers.Remove(normalized.DeviceUdid);
                    }
                }
            }

            return Task.FromResult(Success("ADB keyboard modifier state updated."));
        }

        if (normalized.Phase == "up")
        {
            return Task.FromResult(Success("ADB keyboard key-up acknowledged."));
        }

        var keyCode = HidKeyMapper.Map(normalized.UsageCode);
        if (keyCode is null)
        {
            return Task.FromResult(Failure(
                $"USB HID usage {normalized.UsageCode} is not supported by the ADB keyboard bridge."));
        }

        string[] modifiers;
        lock (stateGate)
        {
            modifiers = pressedModifiers.TryGetValue(normalized.DeviceUdid, out var activeModifiers)
                ? activeModifiers
                    .Order()
                    .Select(HidKeyMapper.Map)
                    .OfType<string>()
                    .ToArray()
                : [];
        }

        return RunInputAsync(
            normalized.DeviceUdid,
            modifiers.Length == 0
                ? ["keyevent", keyCode]
                : ["keycombination", .. modifiers, keyCode],
            "ADB keyboard input delivered.",
            cancellationToken);
    }

    public async Task<InputDeliveryResult> SendTextAsync(
        RemoteTextEvent textEvent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var normalized = textEvent.NormalizeAndValidate();
        foreach (var operation in TextInputEncoder.Encode(normalized.Text))
        {
            var arguments = operation.Kind == TextInputOperationKind.Text
                ? new[] { "text", operation.Value }
                : new[] { "keyevent", operation.Value };
            var result = await RunTextInputAsync(
                normalized.DeviceUdid,
                arguments,
                "ADB text input delivered.",
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Success("ADB text input delivered.");
    }

    private async Task<InputDeliveryResult> RunTextInputAsync(
        string deviceSerial,
        IReadOnlyList<string> inputArguments,
        string successMessage,
        CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = "input "
                          + string.Join(' ', inputArguments.Select(QuoteShellArgument))
                          + "\n";
            var result = await client.RunWithStandardInputAsync(
                ["-s", deviceSerial, "shell", "-T"],
                command,
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                var detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
                return Failure(string.IsNullOrWhiteSpace(detail)
                    ? $"ADB input failed with exit code {result.ExitCode}."
                    : $"ADB input failed: {detail}");
            }

            return Success(successMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex.Message);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static string QuoteShellArgument(string value)
        => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        lock (stateGate)
        {
            gestures.Clear();
            pressedModifiers.Clear();
        }
        sendGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<InputDeliveryResult> DeliverGestureAsync(
        RemoteRuntimeDevice device,
        AndroidPointerGesture gesture,
        CancellationToken cancellationToken)
    {
        var startX = ToDisplayCoordinate(gesture.StartX, device.DisplayWidth!.Value);
        var startY = ToDisplayCoordinate(gesture.StartY, device.DisplayHeight!.Value);
        var endX = ToDisplayCoordinate(gesture.EndX, device.DisplayWidth.Value);
        var endY = ToDisplayCoordinate(gesture.EndY, device.DisplayHeight.Value);
        var distance = Math.Sqrt(
            Math.Pow(gesture.EndX - gesture.StartX, 2)
            + Math.Pow(gesture.EndY - gesture.StartY, 2));
        if (distance <= TapDistanceThreshold)
        {
            return await RunInputAsync(
                device.Identifier,
                ["tap", endX.ToString(CultureInfo.InvariantCulture), endY.ToString(CultureInfo.InvariantCulture)],
                "ADB tap input delivered.",
                cancellationToken).ConfigureAwait(false);
        }

        var duration = Math.Clamp(gesture.EndTimestampMilliseconds - gesture.StartTimestampMilliseconds, 1, 60_000);
        return await RunInputAsync(
            device.Identifier,
            [
                "swipe",
                startX.ToString(CultureInfo.InvariantCulture),
                startY.ToString(CultureInfo.InvariantCulture),
                endX.ToString(CultureInfo.InvariantCulture),
                endY.ToString(CultureInfo.InvariantCulture),
                duration.ToString(CultureInfo.InvariantCulture)
            ],
            "ADB swipe input delivered.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<InputDeliveryResult> DeliverLiveTouchAsync(
        RemoteRuntimeDevice device,
        RemotePointerEvent pointerEvent,
        CancellationToken cancellationToken)
    {
        var pressure = pointerEvent.Phase is "up" or "cancel"
            ? 0
            : ActiveTouchPressure;
        var touches = new List<AndroidEmulatorTouch>(2)
        {
            new(
                ToDisplayCoordinate(pointerEvent.NormalizedX, device.DisplayWidth!.Value),
                ToDisplayCoordinate(pointerEvent.NormalizedY, device.DisplayHeight!.Value),
                0,
                pressure),
        };
        if (pointerEvent.SecondaryContact is not null)
        {
            touches.Add(new AndroidEmulatorTouch(
                ToDisplayCoordinate(pointerEvent.SecondaryContact.NormalizedX, device.DisplayWidth.Value),
                ToDisplayCoordinate(pointerEvent.SecondaryContact.NormalizedY, device.DisplayHeight.Value),
                1,
                pressure));
        }

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await controller!.SendTouchAsync(
                device.Identifier,
                touches,
                cancellationToken).ConfigureAwait(false);
            return Success(pointerEvent.SecondaryContact is null
                ? "Android Emulator live touch input delivered."
                : "Android Emulator live multi-touch input delivered.");
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task<InputDeliveryResult> RunInputAsync(
        string deviceSerial,
        IReadOnlyList<string> inputArguments,
        string successMessage,
        CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var arguments = new List<string> { "-s", deviceSerial, "shell", "input" };
            arguments.AddRange(inputArguments);
            var result = await client.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                var detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
                return Failure(string.IsNullOrWhiteSpace(detail)
                    ? $"ADB input failed with exit code {result.ExitCode}."
                    : $"ADB input failed: {detail}");
            }

            return Success(successMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(ex.Message);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private RemoteRuntimeDevice? ResolveReadyDevice(string deviceSerial)
    {
        var device = deviceResolver(deviceSerial);
        return device is { IsBooted: true, DisplayWidth: > 0, DisplayHeight: > 0 }
            ? device
            : null;
    }

    private InputDeliveryResult Success(string message)
    {
        SetStatus(message);
        return InputDeliveryResult.Success(BackendName, message);
    }

    private InputDeliveryResult Failure(string message)
    {
        SetStatus(message);
        return InputDeliveryResult.Failure(BackendName, message);
    }

    private InputDeliveryResult FailureLocked(string message)
    {
        status = message;
        return InputDeliveryResult.Failure(BackendName, message);
    }

    private void SetStatus(string value)
    {
        lock (stateGate)
        {
            status = value;
        }
    }

    private static int ToDisplayCoordinate(double normalized, int size)
        => (int)Math.Round(Math.Clamp(normalized, 0d, 1d) * Math.Max(0, size - 1));

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim()
           ?? string.Empty;

    private readonly record struct AndroidPointerKey(string DeviceSerial, long PointerId);

    private sealed record AndroidPointerGesture(
        double StartX,
        double StartY,
        double EndX,
        double EndY,
        long StartTimestampMilliseconds,
        long EndTimestampMilliseconds)
    {
        public static AndroidPointerGesture Start(RemotePointerEvent pointerEvent)
            => new(
                pointerEvent.NormalizedX,
                pointerEvent.NormalizedY,
                pointerEvent.NormalizedX,
                pointerEvent.NormalizedY,
                pointerEvent.TimestampMilliseconds,
                pointerEvent.TimestampMilliseconds);

        public AndroidPointerGesture Update(RemotePointerEvent pointerEvent)
            => this with
            {
                EndX = pointerEvent.NormalizedX,
                EndY = pointerEvent.NormalizedY,
                EndTimestampMilliseconds = pointerEvent.TimestampMilliseconds,
            };
    }
}
