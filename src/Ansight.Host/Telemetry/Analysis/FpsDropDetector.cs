namespace Ansight.Host.Telemetry.Analysis;

internal static class FpsDropDetector
{
    private const int CriticalFpsThreshold = 30;
    private const int SignificantFpsDropThreshold = 20;
    private const int SignificantFpsDropCeiling = 40;
    private const int HealthyFpsBaseline = 50;
    private static readonly TimeSpan FpsDropMergeGap = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TelemetryAnalysisEventDuration = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<FpsDropAnalysisWindow> AnalyzeFpsDrops(MetricChartState chart)
    {
        ArgumentNullException.ThrowIfNull(chart);

        var candidates = new List<FpsDropAnalysisWindow>();
        foreach (var channelGroup in chart.Metrics
                     .Where(sample => MetricChannelClassification.IsFpsChannel(chart.Channels, sample.ChannelId))
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

            AddFpsDropAnalysisWindows(candidates, samples, channelGroup.Key);
        }

        return candidates;
    }

    private static void AddFpsDropAnalysisWindows(
        ICollection<FpsDropAnalysisWindow> candidates,
        IReadOnlyList<SessionMetricSample> samples,
        byte channelId)
    {
        SessionMetricSample? activeBaseline = null;
        SessionMetricSample? activeLast = null;
        SessionMetricSample? activeMinimum = null;
        SessionMetricSample? previous = null;

        foreach (var sample in samples)
        {
            if (previous is null)
            {
                previous = sample;
                continue;
            }

            if (activeBaseline is not null && activeLast is not null && activeMinimum is not null)
            {
                var shouldContinueDropWindow =
                    sample.CapturedAtUtc - activeLast.CapturedAtUtc <= FpsDropMergeGap
                    && IsDroppedFpsSample(sample);
                if (!shouldContinueDropWindow)
                {
                    AddFpsDropAnalysisWindow(candidates, channelId, activeBaseline, activeMinimum);
                    activeBaseline = null;
                    activeLast = null;
                    activeMinimum = null;
                }
                else
                {
                    activeLast = sample;
                    if (sample.Value < activeMinimum.Value)
                    {
                        activeMinimum = sample;
                    }

                    previous = sample;
                    continue;
                }
            }

            if (IsMajorFpsDropBoundary(sample, previous))
            {
                activeBaseline = previous;
                activeLast = sample;
                activeMinimum = sample;
            }

            previous = sample;
        }

        if (activeBaseline is not null && activeMinimum is not null)
        {
            AddFpsDropAnalysisWindow(candidates, channelId, activeBaseline, activeMinimum);
        }
    }

    private static void AddFpsDropAnalysisWindow(
        ICollection<FpsDropAnalysisWindow> candidates,
        byte channelId,
        SessionMetricSample baseline,
        SessionMetricSample minimum)
    {
        var startUtc = baseline.CapturedAtUtc;
        var endUtc = minimum.CapturedAtUtc > startUtc
            ? minimum.CapturedAtUtc
            : startUtc + TelemetryAnalysisEventDuration;
        candidates.Add(new FpsDropAnalysisWindow(
            channelId,
            baseline,
            minimum,
            startUtc,
            endUtc));
    }

    private static bool IsMajorFpsDropBoundary(SessionMetricSample sample, SessionMetricSample previous)
    {
        if (sample.Value >= previous.Value || previous.Value <= SignificantFpsDropCeiling)
        {
            return false;
        }

        var drop = previous.Value - sample.Value;
        if (drop < SignificantFpsDropThreshold)
        {
            return false;
        }

        return sample.Value <= CriticalFpsThreshold
               || previous.Value >= HealthyFpsBaseline && sample.Value <= SignificantFpsDropCeiling;
    }

    private static bool IsDroppedFpsSample(SessionMetricSample sample)
    {
        return sample.Value <= SignificantFpsDropCeiling;
    }

}
