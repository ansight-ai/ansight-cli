using System.Diagnostics;
using Ansight.MacSimulatorHid;

namespace Ansight.Host.Devices;

public sealed partial class HeadlessHostDeviceDriver
{
    public Task<UiInputResult> StylusAsync(UiStylusRequest request, CancellationToken cancellationToken = default)
        => ExecuteInputAsync(request.DeviceIdentifier, async (device, token) =>
        {
            if (device.Platform != DevicePlatforms.Ios || device.IsPhysical)
                return UiInputResult.Failure("Stylus HID v1 requires an iOS Simulator.");
            var session = await GetSimulatorHidSessionAsync(device.Identifier, token).ConfigureAwait(false);
            var pointerId = (uint)Interlocked.Increment(ref nextPointerId);
            MacSimulatorStylusSample? last = null;
            var clock = Stopwatch.StartNew();
            try
            {
                foreach (var item in request.Samples)
                {
                    var wait = item.OffsetMilliseconds - clock.Elapsed.TotalMilliseconds;
                    if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var sample = session.StampStylus(item.Sample);
                    if (request.BeforeSample is not null) await request.BeforeSample(sample, token).ConfigureAwait(false);
                    session.SendStylus(device.Identifier, sample, pointerId);
                    last = sample;
                }
            }
            finally
            {
                // Release contact/proximity even when task cancellation interrupts the trajectory.
                if (last is not null && last.Phase != MacSimulatorStylusPhase.HoverExit)
                {
                    if (last.Phase is MacSimulatorStylusPhase.Down or MacSimulatorStylusPhase.Move)
                        session.SendStylus(device.Identifier, session.StampStylus(last with { Phase = MacSimulatorStylusPhase.Cancel, Pressure = 0 }), pointerId);
                    session.SendStylus(device.Identifier, session.StampStylus(last with { Phase = MacSimulatorStylusPhase.HoverExit, Pressure = 0 }), pointerId);
                }
            }
            return new UiInputResult(true, IosBackend,
                "Stylus sequence delivered. Verify received Pencil fields in the target app; hover, pressure and orientation support depends on the Simulator runtime.");
        }, cancellationToken);
}
