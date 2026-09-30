using System.Globalization;

namespace Ansight.Host.Replay;

/// <summary>
/// Assesses whether screenshots are frequent enough to ground replay extraction.
/// </summary>
public static class ReplayFrameCadenceAnalyzer
{
    public const double CoverageWindowMilliseconds = 1_500;
    public const double ReliableStepCoveragePercent = 90;
    public const double UnreliableStepCoveragePercent = 60;
    public const double ReliableDistinctSupportingFramePercent = 90;
    public const double UnreliableDistinctSupportingFramePercent = 60;

    public static ReplayFrameCadence Analyze(
        IReadOnlyList<SessionImageFrame> images,
        IReadOnlyList<ReplayStep> replaySteps,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(replaySteps);
        rangeStartUtc = rangeStartUtc.ToUniversalTime();
        rangeEndUtc = rangeEndUtc.ToUniversalTime();
        if (rangeEndUtc < rangeStartUtc)
        {
            (rangeStartUtc, rangeEndUtc) = (rangeEndUtc, rangeStartUtc);
        }

        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new ArgumentException("The cadence analysis range must have a positive duration.");
        }

        var frames = images
            .Where(frame => frame.CapturedAtUtc >= rangeStartUtc && frame.CapturedAtUtc <= rangeEndUtc)
            .OrderBy(static frame => frame.CapturedAtUtc)
            .ThenBy(static frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        var frameTimes = frames
            .Select(static frame => frame.CapturedAtUtc.ToUniversalTime())
            .ToArray();
        var intervals = new double[Math.Max(0, frameTimes.Length - 1)];
        for (var index = 1; index < frameTimes.Length; index++)
        {
            intervals[index - 1] = Math.Max(
                0,
                (frameTimes[index] - frameTimes[index - 1]).TotalMilliseconds);
        }

        var boundaryAndFrameGaps = new List<double>(intervals.Length + 2);
        if (frameTimes.Length == 0)
        {
            boundaryAndFrameGaps.Add((rangeEndUtc - rangeStartUtc).TotalMilliseconds);
        }
        else
        {
            boundaryAndFrameGaps.Add(Math.Max(0, (frameTimes[0] - rangeStartUtc).TotalMilliseconds));
            boundaryAndFrameGaps.AddRange(intervals);
            boundaryAndFrameGaps.Add(Math.Max(0, (rangeEndUtc - frameTimes[^1]).TotalMilliseconds));
        }

        var timedSteps = replaySteps
            .Where(static step => step.CapturedAtUtc.HasValue)
            .ToArray();
        var stepFrameMatches = timedSteps
            .Select(step => FindNearestFrame(
                frames,
                step.CapturedAtUtc!.Value.ToUniversalTime()))
            .ToArray();
        var coveredMatches = stepFrameMatches
            .Where(static match => match is { DistanceMilliseconds: <= CoverageWindowMilliseconds })
            .Select(static match => match!.Value)
            .ToArray();
        var coveredStepCount = coveredMatches.Length;
        var stepCoveragePercent = timedSteps.Length == 0
            ? (double?)null
            : coveredStepCount * 100d / timedSteps.Length;
        var distinctSupportingFrameCount = CountDistinctSupportingFrames(frames, timedSteps);
        var distinctSupportingFramePercent = timedSteps.Length == 0
            ? (double?)null
            : distinctSupportingFrameCount * 100d / timedSteps.Length;
        var stepFrameDistances = stepFrameMatches
            .Where(static match => match.HasValue)
            .Select(static match => match!.Value.DistanceMilliseconds)
            .ToArray();
        var medianStepFrameDistance = Percentile(stepFrameDistances, 0.5);
        var p95StepFrameDistance = Percentile(stepFrameDistances, 0.95);
        var maximumStepFrameDistance = stepFrameDistances.Length == 0
            ? (double?)null
            : stepFrameDistances.Max();
        var rangeDurationSeconds = (rangeEndUtc - rangeStartUtc).TotalSeconds;
        var effectiveFramesPerSecond = frameTimes.Length < 2
            ? 0
            : (frameTimes.Length - 1) / Math.Max(
                double.Epsilon,
                (frameTimes[^1] - frameTimes[0]).TotalSeconds);
        var medianInterval = Percentile(intervals, 0.5);
        var p95Interval = Percentile(intervals, 0.95);
        var maximumGap = boundaryAndFrameGaps.Max();
        var findings = BuildFindings(
            frameTimes.Length,
            timedSteps.Length,
            stepCoveragePercent,
            distinctSupportingFramePercent);
        var rating = DetermineRating(
            frameTimes.Length,
            timedSteps.Length,
            stepCoveragePercent,
            distinctSupportingFramePercent);
        var allowsLiveReplay = string.Equals(rating, "reliable", StringComparison.Ordinal);
        var message = BuildMessage(
            rating,
            frames.Length,
            rangeDurationSeconds,
            effectiveFramesPerSecond,
            medianInterval,
            p95Interval,
            maximumGap,
            timedSteps.Length,
            coveredStepCount,
            stepCoveragePercent,
            distinctSupportingFrameCount,
            distinctSupportingFramePercent,
            medianStepFrameDistance,
            p95StepFrameDistance,
            maximumStepFrameDistance,
            findings);

        return new ReplayFrameCadence(
            rating,
            allowsLiveReplay,
            message,
            frames.Length,
            rangeDurationSeconds,
            effectiveFramesPerSecond,
            medianInterval,
            p95Interval,
            maximumGap,
            timedSteps.Length,
            coveredStepCount,
            stepCoveragePercent,
            CoverageWindowMilliseconds,
            distinctSupportingFrameCount,
            distinctSupportingFramePercent,
            medianStepFrameDistance,
            p95StepFrameDistance,
            maximumStepFrameDistance,
            findings);
    }

    private static string DetermineRating(
        int frameCount,
        int timedStepCount,
        double? stepCoveragePercent,
        double? distinctSupportingFramePercent)
    {
        if (frameCount < 2
            || (timedStepCount > 0
                && (stepCoveragePercent < UnreliableStepCoveragePercent
                    || distinctSupportingFramePercent < UnreliableDistinctSupportingFramePercent)))
        {
            return "unreliable";
        }

        if (timedStepCount == 0
            || (stepCoveragePercent >= ReliableStepCoveragePercent
                && distinctSupportingFramePercent >= ReliableDistinctSupportingFramePercent))
        {
            return "reliable";
        }

        return "degraded";
    }

    private static IReadOnlyList<string> BuildFindings(
        int frameCount,
        int timedStepCount,
        double? stepCoveragePercent,
        double? distinctSupportingFramePercent)
    {
        var findings = new List<string>();
        if (frameCount < 2)
        {
            findings.Add("fewer than two screenshot frames were captured in the selected range");
        }

        if (timedStepCount > 0 && stepCoveragePercent < ReliableStepCoveragePercent)
        {
            findings.Add($"fewer than {ReliableStepCoveragePercent:0}% of replay steps have a nearby frame");
        }

        if (timedStepCount > 0
            && distinctSupportingFramePercent < ReliableDistinctSupportingFramePercent)
        {
            findings.Add(
                $"fewer than {ReliableDistinctSupportingFramePercent:0}% of replay steps have distinct supporting change frames");
        }

        return findings;
    }

    private static string BuildMessage(
        string rating,
        int frameCount,
        double rangeDurationSeconds,
        double effectiveFramesPerSecond,
        double? medianInterval,
        double? p95Interval,
        double maximumGap,
        int timedStepCount,
        int coveredStepCount,
        double? stepCoveragePercent,
        int distinctSupportingFrameCount,
        double? distinctSupportingFramePercent,
        double? medianStepFrameDistance,
        double? p95StepFrameDistance,
        double? maximumStepFrameDistance,
        IReadOnlyList<string> findings)
    {
        var message = $"Frame support is {rating}: {frameCount:N0} retained change frame(s) across "
                      + $"{rangeDurationSeconds:0.0} s ({effectiveFramesPerSecond:0.00} retained change fps); "
                      + $"observed median interval {FormatDuration(medianInterval)}, "
                      + $"p95 {FormatDuration(p95Interval)}, longest retained-frame gap {FormatDuration(maximumGap)}";
        if (timedStepCount > 0)
        {
            message += $"; {coveredStepCount:N0}/{timedStepCount:N0} replay step(s) "
                       + $"({stepCoveragePercent:0.#}%) have a frame within "
                       + $"{FormatDuration(CoverageWindowMilliseconds)}; "
                       + $"{distinctSupportingFrameCount:N0} distinct supporting frame(s) "
                       + $"({distinctSupportingFramePercent:0.#}% of steps); "
                       + $"nearest-frame distance median {FormatDuration(medianStepFrameDistance)}, "
                       + $"p95 {FormatDuration(p95StepFrameDistance)}, "
                       + $"maximum {FormatDuration(maximumStepFrameDistance)}";
        }

        message += ". Retained-frame gaps are informational because exact consecutive duplicate images are suppressed.";
        if (findings.Count > 0)
        {
            message += " " + string.Join("; ", findings.Select(static finding =>
                char.ToUpperInvariant(finding[0]) + finding[1..])) + ".";
        }

        return message;
    }

    private static int CountDistinctSupportingFrames(
        IReadOnlyList<SessionImageFrame> frames,
        IReadOnlyList<ReplayStep> timedSteps)
    {
        var distinctFrames = frames
            .DistinctBy(static frame => frame.FrameId, StringComparer.Ordinal)
            .ToArray();
        var nextFrameIndex = 0;
        var supportingFrameCount = 0;

        // Each step has the same coverage window. Matching steps chronologically to
        // the earliest available frame preserves later frames for later steps and
        // maximizes distinct support. Counting only nearest frames can reject two
        // steps that share a nearest image even when both have other nearby images.
        foreach (var step in timedSteps.OrderBy(static step => step.CapturedAtUtc))
        {
            var capturedAtUtc = step.CapturedAtUtc!.Value.ToUniversalTime();
            while (nextFrameIndex < distinctFrames.Length
                   && (capturedAtUtc - distinctFrames[nextFrameIndex].CapturedAtUtc).TotalMilliseconds
                   > CoverageWindowMilliseconds)
            {
                nextFrameIndex++;
            }

            if (nextFrameIndex < distinctFrames.Length
                && (distinctFrames[nextFrameIndex].CapturedAtUtc - capturedAtUtc).TotalMilliseconds
                <= CoverageWindowMilliseconds)
            {
                supportingFrameCount++;
                nextFrameIndex++;
            }
        }

        return supportingFrameCount;
    }

    private static FrameMatch? FindNearestFrame(
        IReadOnlyList<SessionImageFrame> frames,
        DateTimeOffset capturedAtUtc)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        var nearest = frames
            .Select(frame => new FrameMatch(
                frame.FrameId,
                Math.Abs((frame.CapturedAtUtc.ToUniversalTime() - capturedAtUtc).TotalMilliseconds)))
            .OrderBy(static match => match.DistanceMilliseconds)
            .ThenBy(static match => match.FrameId, StringComparer.Ordinal)
            .First();
        return nearest;
    }

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToArray();
        var rank = Math.Max(1, (int)Math.Ceiling(percentile * ordered.Length));
        return ordered[rank - 1];
    }

    private static string FormatDuration(double? milliseconds)
    {
        if (!milliseconds.HasValue)
        {
            return "n/a";
        }

        return milliseconds.Value >= 1_000
            ? (milliseconds.Value / 1_000).ToString("0.0 s", CultureInfo.InvariantCulture)
            : milliseconds.Value.ToString("0 ms", CultureInfo.InvariantCulture);
    }

    private readonly record struct FrameMatch(string FrameId, double DistanceMilliseconds);
}
