namespace Ansight.Host.Telemetry.Analysis;

internal static class MemorySpikeDetector
{
    private const double MemorySpikeRiseStartRatio = 0.1d;
    private const double MemorySpikeRetentionRatio = 0.5d;
    private const double MemorySpikePeakPlateauRatio = 0.02d;
    private static readonly TimeSpan MemorySpikeRiseWindow = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MemorySpikePeakSearchWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MemorySpikePeakHoldWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumMemorySpikeAnnotationDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MemorySpikeCooldown = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TelemetryAnalysisEventDuration = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<MemorySpikeAnalysisWindow> AnalyzeMemorySpikes(
        MetricChartState chart,
        MemorySpikeTelemetryAnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);

        options ??= MemorySpikeTelemetryAnalysisOptions.Default;
        var candidates = new List<MemorySpikeAnalysisWindow>();
        foreach (var channelGroup in chart.Metrics
                     .Where(sample => MetricChannelClassification.IsMemoryChannel(chart.Channels, sample.ChannelId))
                     .GroupBy(sample => sample.ChannelId)
                     .OrderBy(group => group.Key))
        {
            var samples = channelGroup
                .OrderBy(sample => sample.CapturedAtUtc)
                .ToArray();
            if (samples.Length < 2)
            {
                continue;
            }

            for (var index = 1; index < samples.Length; index++)
            {
                if (TryCreateMemorySpikeAnalysisWindow(samples, index, channelGroup.Key, options, out var candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return SelectDistinctMemorySpikeWindows(candidates);
    }

    private static bool TryCreateMemorySpikeAnalysisWindow(
        IReadOnlyList<SessionMetricSample> samples,
        int currentIndex,
        byte channelId,
        MemorySpikeTelemetryAnalysisOptions options,
        out MemorySpikeAnalysisWindow analysisWindow)
    {
        analysisWindow = default!;

        var peakIndex = ResolveMemorySpikePeakIndex(samples, currentIndex);
        var floorBaselineIndex = ResolveMemorySpikeFloorBaselineIndex(samples, peakIndex);
        if (floorBaselineIndex >= peakIndex)
        {
            return false;
        }

        var floorBaseline = samples[floorBaselineIndex];
        var peak = samples[peakIndex];
        var floorDeltaBytes = peak.Value - floorBaseline.Value;
        if (floorDeltaBytes <= 0)
        {
            return false;
        }

        var startIndex = ResolveMemorySpikeStartIndex(samples, floorBaselineIndex, peakIndex, floorBaseline, floorDeltaBytes);
        var baselineIndex = Math.Max(floorBaselineIndex, startIndex - 1);
        var baseline = samples[baselineIndex];
        var deltaBytes = peak.Value - baseline.Value;
        var minimumDeltaBytes = Math.Max(
            options.MinimumMemorySpikeBytes,
            (long)Math.Ceiling(Math.Max(1, baseline.Value) * options.MinimumMemorySpikeRatio));
        if (deltaBytes < minimumDeltaBytes)
        {
            return false;
        }

        var startUtc = samples[startIndex].CapturedAtUtc;
        var endUtc = ResolveMemorySpikeEndUtc(peak, startUtc);
        var retainedUntilUtc = ResolveMemorySpikeRetainedUntilUtc(samples, peakIndex, baseline, deltaBytes, endUtc);

        analysisWindow = new MemorySpikeAnalysisWindow(
            channelId,
            baseline,
            peak,
            startUtc,
            endUtc,
            deltaBytes,
            retainedUntilUtc);
        return true;
    }

    private static int ResolveMemorySpikePeakIndex(IReadOnlyList<SessionMetricSample> samples, int currentIndex)
    {
        var peakIndex = currentIndex;
        var searchEndUtc = samples[currentIndex].CapturedAtUtc + MemorySpikePeakSearchWindow;
        for (var index = currentIndex + 1; index < samples.Count; index++)
        {
            var candidate = samples[index];
            if (candidate.CapturedAtUtc > searchEndUtc)
            {
                break;
            }

            if (candidate.Value > samples[peakIndex].Value)
            {
                peakIndex = index;
            }
        }

        var peakValue = samples[peakIndex].Value;
        var nearPeakThreshold = peakValue - Math.Max(1L, (long)Math.Ceiling(peakValue * MemorySpikePeakPlateauRatio));
        for (var index = currentIndex; index < peakIndex; index++)
        {
            if (samples[index].Value >= nearPeakThreshold)
            {
                return index;
            }
        }

        return peakIndex;
    }

    private static int ResolveMemorySpikeFloorBaselineIndex(IReadOnlyList<SessionMetricSample> samples, int peakIndex)
    {
        var peak = samples[peakIndex];
        var windowStartUtc = peak.CapturedAtUtc - MemorySpikeRiseWindow;
        var baselineIndex = peakIndex - 1;
        for (var index = peakIndex - 1; index >= 0; index--)
        {
            var candidate = samples[index];
            if (candidate.CapturedAtUtc < windowStartUtc)
            {
                break;
            }

            if (candidate.Value < samples[baselineIndex].Value)
            {
                baselineIndex = index;
            }
        }

        return baselineIndex;
    }

    private static int ResolveMemorySpikeStartIndex(
        IReadOnlyList<SessionMetricSample> samples,
        int floorBaselineIndex,
        int peakIndex,
        SessionMetricSample floorBaseline,
        long deltaBytes)
    {
        var riseStartThreshold = floorBaseline.Value + Math.Max(1, (long)Math.Ceiling(deltaBytes * MemorySpikeRiseStartRatio));
        for (var index = floorBaselineIndex + 1; index <= peakIndex; index++)
        {
            if (samples[index].Value <= riseStartThreshold)
            {
                continue;
            }

            if (SamplesRemainAboveThreshold(samples, index, peakIndex, riseStartThreshold))
            {
                return index;
            }
        }

        return Math.Min(peakIndex, floorBaselineIndex + 1);
    }

    private static bool SamplesRemainAboveThreshold(
        IReadOnlyList<SessionMetricSample> samples,
        int startIndex,
        int endIndex,
        long threshold)
    {
        for (var index = startIndex; index <= endIndex; index++)
        {
            if (samples[index].Value <= threshold)
            {
                return false;
            }
        }

        return true;
    }

    private static DateTimeOffset ResolveMemorySpikeEndUtc(SessionMetricSample peak, DateTimeOffset startUtc)
    {
        var latestEndUtc = startUtc + MaximumMemorySpikeAnnotationDuration;
        var endUtc = peak.CapturedAtUtc + MemorySpikePeakHoldWindow;
        if (endUtc <= startUtc)
        {
            endUtc = startUtc + TelemetryAnalysisEventDuration;
        }

        return endUtc > latestEndUtc ? latestEndUtc : endUtc;
    }

    private static DateTimeOffset? ResolveMemorySpikeRetainedUntilUtc(
        IReadOnlyList<SessionMetricSample> samples,
        int peakIndex,
        SessionMetricSample baseline,
        long deltaBytes,
        DateTimeOffset eventEndUtc)
    {
        var retainedThreshold = baseline.Value + Math.Max(1, (long)Math.Ceiling(deltaBytes * MemorySpikeRetentionRatio));
        DateTimeOffset? retainedUntilUtc = null;
        for (var index = peakIndex + 1; index < samples.Count; index++)
        {
            var candidate = samples[index];
            if (candidate.Value < retainedThreshold)
            {
                break;
            }

            retainedUntilUtc = candidate.CapturedAtUtc;
        }

        return retainedUntilUtc.HasValue && retainedUntilUtc.Value > eventEndUtc
            ? retainedUntilUtc
            : null;
    }

    private static IReadOnlyList<MemorySpikeAnalysisWindow> SelectDistinctMemorySpikeWindows(
        IReadOnlyList<MemorySpikeAnalysisWindow> candidates)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<MemorySpikeAnalysisWindow>();
        }

        var selected = new List<MemorySpikeAnalysisWindow>();
        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.DeltaBytes)
                     .ThenBy(candidate => candidate.EndUtc - candidate.StartUtc)
                     .ThenBy(candidate => candidate.StartUtc))
        {
            if (selected.Any(existing => ShouldSuppressCandidate(candidate, existing)))
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected
            .OrderBy(candidate => candidate.StartUtc)
            .ThenBy(candidate => candidate.ChannelId)
            .ThenBy(candidate => candidate.Peak.CapturedAtUtc)
            .ToArray();
    }

    private static bool ShouldSuppressCandidate(
        MemorySpikeAnalysisWindow candidate,
        MemorySpikeAnalysisWindow existing)
    {
        if (candidate.ChannelId != existing.ChannelId)
        {
            return false;
        }

        return WindowsOverlap(candidate, existing)
               || (candidate.Peak.CapturedAtUtc - existing.Peak.CapturedAtUtc).Duration() < MemorySpikeCooldown;
    }

    private static bool WindowsOverlap(
        MemorySpikeAnalysisWindow left,
        MemorySpikeAnalysisWindow right)
    {
        return left.StartUtc <= right.EndUtc && right.StartUtc <= left.EndUtc;
    }

}
