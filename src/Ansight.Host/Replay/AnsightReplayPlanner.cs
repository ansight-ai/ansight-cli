namespace Ansight.Host.Replay;

/// <summary>
/// Builds a bounded agentic replay plan from an Ansight session capture.
/// </summary>
public static class AnsightReplayPlanner
{
    public const int MaximumReplaySteps = 50;

    public static ReplayPlan Build(
        AppSessionSnapshot snapshot,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var replayStartUtc = (startUtc ?? snapshot.CreatedUtc).ToUniversalTime();
        var replayEndUtc = (endUtc ?? snapshot.LastUpdatedUtc).ToUniversalTime();
        if (replayEndUtc < replayStartUtc)
        {
            var earlierUtc = replayEndUtc;
            replayEndUtc = replayStartUtc;
            replayStartUtc = earlierUtc;
        }

        if (replayEndUtc <= replayStartUtc)
        {
            throw new ArgumentException("The replay range must have a positive duration.");
        }

        var extraction = TimelineTaskExtractor.Extract(
            snapshot,
            replayStartUtc,
            replayEndUtc,
            snapshot.Name ?? $"Replay {snapshot.AppId}",
            includeCoordinateFallbackTaps: true);
        var diagnostics = extraction.Diagnostics.ToList();
        var replaySteps = extraction.ReplaySteps
            .Take(MaximumReplaySteps)
            .ToArray();
        if (!snapshot.VisualTreeSnapshots.Any(tree =>
                tree.CapturedAtUtc >= replayStartUtc
                && tree.CapturedAtUtc <= replayEndUtc))
        {
            diagnostics.Add(
                "The selected range contains no visual-tree snapshots, so text entry and clear-field actions "
                + "cannot be inferred. Use a capture mode that records visual trees.");
        }

        if (extraction.ReplaySteps.Count > MaximumReplaySteps)
        {
            diagnostics.Add(
                $"The capture produced {extraction.ReplaySteps.Count:N0} replayable actions; "
                + $"only the first {MaximumReplaySteps:N0} can be sent to one agent run.");
        }

        var steps = replaySteps
            .Select((step, index) => step with { Sequence = index + 1 })
            .ToArray();
        var sourceEventCount = snapshot.Touches.Count(touch =>
                                   touch.CapturedAtUtc >= replayStartUtc
                                   && touch.CapturedAtUtc <= replayEndUtc)
                               + snapshot.VisualTreeSnapshots.Count(tree =>
                                   tree.CapturedAtUtc >= replayStartUtc
                                   && tree.CapturedAtUtc <= replayEndUtc);
        var frameCadence = ReplayFrameCadenceAnalyzer.Analyze(
            snapshot.Images,
            steps,
            replayStartUtc,
            replayEndUtc);
        return new ReplayPlan(
            "ansight.replay-plan/v1",
            "ansight",
            snapshot.SessionId,
            snapshot.AppId,
            sourceEventCount,
            steps,
            diagnostics)
        {
            FrameCadence = frameCadence
        };
    }
}
