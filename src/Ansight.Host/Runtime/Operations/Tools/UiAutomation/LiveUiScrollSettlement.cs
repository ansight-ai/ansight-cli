namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class LiveUiScrollSettlement
{
    private const int MaximumSamples = 8;
    private const int SampleIntervalMilliseconds = 150;

    public static async Task<SettledScrollCapture> CaptureAsync(
        LiveUiCaptureResult initial, LiveUiSelector selector,
        Func<CancellationToken, Task<LiveUiCaptureResult>> capture,
        CancellationToken cancellationToken)
    {
        var previous = initial;
        var unchangedSamples = 0;

        for (var sample = 0; sample < MaximumSamples; sample++)
        {
            if (previous.Capture is not { } before || before.Viewport is null)
            {
                return new SettledScrollCapture(previous, false);
            }

            var matches = LiveUiNodeQuery.PrioritizeActionTargets(
                LiveUiNodeQuery.Find(before.Root, selector, before.TypeRegistry), before.Payload, before.Viewport);

            if (matches.Count <= selector.Index)
            {
                return new SettledScrollCapture(previous, false);
            }

            await Task.Delay(SampleIntervalMilliseconds, cancellationToken).ConfigureAwait(false);

            var next = await capture(cancellationToken).ConfigureAwait(false);
            var observation = LiveUiScrollObservation.Compare(before, next.Capture, matches[selector.Index], selector);
            var status = observation["status"]?.GetValue<string>();
            unchangedSamples = status == "unchanged" ? unchangedSamples + 1 : 0;
            previous = next;

            if (unchangedSamples >= 2)
            {
                return new SettledScrollCapture(next, true);
            }

            if (status == "unknown")
            {
                return new SettledScrollCapture(next, false);
            }
        }

        // Inertia, edge bounce, and continuously changing content cannot prove
        // movement or a boundary until the observed viewport is stable.
        return new SettledScrollCapture(previous, false);
    }
}
