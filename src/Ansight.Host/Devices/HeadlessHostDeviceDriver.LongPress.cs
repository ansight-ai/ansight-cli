using Ansight.MacSimulatorHid;

namespace Ansight.Host.Devices;

public sealed partial class HeadlessHostDeviceDriver
{
    public Task<UiInputResult> LongPressAsync(
        UiLongPressRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        return ExecuteInputAsync(request.DeviceIdentifier, async (device, token) =>
        {
            if (device.Platform == DevicePlatforms.Android)
            {
                var size = await GetAndroidDisplaySizeAsync(device.Identifier, token).ConfigureAwait(false);
                var x = ToDisplayCoordinate(request.NormalizedX, size.Width);
                var y = ToDisplayCoordinate(request.NormalizedY, size.Height);
                // Android input implements a stationary hold as a swipe with identical endpoints.
                var result = await devices.SendSwipeAsync(device.Platform, device.Identifier,
                    x, y, x, y, request.DurationMilliseconds, token).ConfigureAwait(false);
                return ToInputResult(result, AndroidBackend);
            }

            if (device.IsPhysical)
            {
                await iosPhysicalDeviceInput.LongPressAsync(device.Identifier,
                    RequireApplicationIdentifier(request.ApplicationIdentifier), request.NormalizedX,
                    request.NormalizedY, request.DurationMilliseconds, token).ConfigureAwait(false);
                return Success(IosPhysicalBackend, "Physical iOS long press delivered through Appium/XCUITest.");
            }

            var session = await GetSimulatorHidSessionAsync(device.Identifier, token).ConfigureAwait(false);
            var pointerId = Interlocked.Increment(ref nextPointerId);
            await HoldSimulatorPointerAsync(phase => session.SendPointer(device.Identifier, phase,
                    request.NormalizedX, request.NormalizedY, pointerId, Environment.TickCount64),
                request.DurationMilliseconds, token).ConfigureAwait(false);
            return Success(IosBackend, "iOS Simulator long press delivered.");
        }, cancellationToken);
    }

    internal static async Task HoldSimulatorPointerAsync(
        Action<MacSimulatorPointerPhase> sendPointer, int durationMilliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sendPointer(MacSimulatorPointerPhase.Down);
        try
        {
            await Task.Delay(durationMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Release even when the caller cancels during the hold.
            sendPointer(MacSimulatorPointerPhase.Up);
        }
    }
}
