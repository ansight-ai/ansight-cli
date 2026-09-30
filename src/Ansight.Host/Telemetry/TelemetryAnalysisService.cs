namespace Ansight.Host.Telemetry;

public sealed class TelemetryAnalysisService
{
    private const long BytesPerMegabyte = 1024L * 1024L;
    private const int CriticalFpsThreshold = 30;

    public IReadOnlyList<FpsDropTelemetryEvent> AnalyzeFpsDrops(
        IReadOnlyList<SessionMetricSample> metrics,
        IReadOnlyCollection<SessionMetricChannel> channels,
        CancellationToken cancellationToken = default)
    {
        var chart = CreateChart(metrics, channels);
        cancellationToken.ThrowIfCancellationRequested();

        var windows = FpsDropDetector.AnalyzeFpsDrops(chart);
        var events = new List<FpsDropTelemetryEvent>(windows.Count);
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var minimumFps = Math.Max(0, window.Minimum.Value);
            events.Add(new FpsDropTelemetryEvent(
                window.ChannelId,
                ResolveChannelName(chart.Channels, window.ChannelId),
                window.Baseline,
                window.Minimum,
                window.StartUtc.ToUniversalTime(),
                window.EndUtc.ToUniversalTime(),
                Math.Max(0, window.Baseline.Value - window.Minimum.Value),
                ResolveFpsSeverity(minimumFps),
                Math.Max(0, 60 - minimumFps)));
        }

        return events;
    }

    public IReadOnlyList<MemorySpikeTelemetryEvent> AnalyzeMemorySpikes(
        IReadOnlyList<SessionMetricSample> metrics,
        IReadOnlyCollection<SessionMetricChannel> channels,
        MemorySpikeAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= MemorySpikeAnalysisOptions.Default;
        Validate(options);

        var chart = CreateChart(metrics, channels);
        cancellationToken.ThrowIfCancellationRequested();
        var detectorOptions = MemorySpikeTelemetryAnalysisOptions.FromAnalysisOptions(options);
        var windows = MemorySpikeDetector.AnalyzeMemorySpikes(chart, detectorOptions);
        var events = new List<MemorySpikeTelemetryEvent>(windows.Count);
        foreach (var window in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deltaPercent = window.Baseline.Value <= 0
                ? 0d
                : window.DeltaBytes / (double)window.Baseline.Value * 100d;
            events.Add(new MemorySpikeTelemetryEvent(
                window.ChannelId,
                ResolveChannelName(chart.Channels, window.ChannelId),
                window.Baseline,
                window.Peak,
                window.StartUtc.ToUniversalTime(),
                window.EndUtc.ToUniversalTime(),
                window.DeltaBytes,
                deltaPercent,
                window.RetainedUntilUtc?.ToUniversalTime(),
                ResolveMemorySeverity(window.DeltaBytes),
                window.DeltaBytes / (double)BytesPerMegabyte));
        }

        return events;
    }

    private static MetricChartState CreateChart(
        IReadOnlyList<SessionMetricSample> metrics,
        IReadOnlyCollection<SessionMetricChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(channels);

        var metricSnapshot = metrics.ToArray();
        var channelMap = new Dictionary<byte, SessionMetricChannel>();
        foreach (var channel in channels)
        {
            ArgumentNullException.ThrowIfNull(channel);
            channelMap[channel.ChannelId] = channel;
        }

        var sessionStartUtc = metricSnapshot.Length == 0
            ? DateTimeOffset.MinValue
            : metricSnapshot.Min(static sample => sample.CapturedAtUtc).ToUniversalTime();
        var sessionEndUtc = metricSnapshot.Length == 0
            ? DateTimeOffset.MinValue
            : metricSnapshot.Max(static sample => sample.CapturedAtUtc).ToUniversalTime();
        return new MetricChartState(
            metricSnapshot,
            channelMap,
            sessionStartUtc,
            sessionEndUtc,
            sessionEndUtc,
            false);
    }

    private static void Validate(MemorySpikeAnalysisOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MinimumIncreasePercent,
            MemorySpikeAnalysisOptions.MinimumAllowedIncreasePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.MinimumIncreasePercent,
            MemorySpikeAnalysisOptions.MaximumAllowedIncreasePercent);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MinimumIncreaseMegabytes,
            MemorySpikeAnalysisOptions.MinimumAllowedIncreaseMegabytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            options.MinimumIncreaseMegabytes,
            MemorySpikeAnalysisOptions.MaximumAllowedIncreaseMegabytes);
    }

    private static string ResolveChannelName(
        IReadOnlyDictionary<byte, SessionMetricChannel> channels,
        byte channelId)
    {
        return channels.TryGetValue(channelId, out var channel) && !string.IsNullOrWhiteSpace(channel.Name)
            ? channel.Name.Trim()
            : $"Channel {channelId}";
    }

    private static TelemetryAnalysisSeverity ResolveFpsSeverity(long fps)
    {
        if (fps < 20)
        {
            return TelemetryAnalysisSeverity.Critical;
        }

        return fps < CriticalFpsThreshold
            ? TelemetryAnalysisSeverity.Severe
            : TelemetryAnalysisSeverity.Major;
    }

    private static TelemetryAnalysisSeverity ResolveMemorySeverity(long deltaBytes)
    {
        var deltaMegabytes = deltaBytes / (double)BytesPerMegabyte;
        if (deltaMegabytes >= 512d)
        {
            return TelemetryAnalysisSeverity.Critical;
        }

        return deltaMegabytes >= 256d
            ? TelemetryAnalysisSeverity.Severe
            : TelemetryAnalysisSeverity.Major;
    }
}
