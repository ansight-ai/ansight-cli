namespace Ansight.Host.Telemetry.Analysis;

using Ansight.Host;

internal sealed record MemorySpikeTelemetryAnalysisOptions(
    long MinimumMemorySpikeBytes,
    double MinimumMemorySpikeRatio)
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    public static MemorySpikeTelemetryAnalysisOptions Default { get; } = FromAnalysisOptions(
        MemorySpikeAnalysisOptions.Default);

    public static MemorySpikeTelemetryAnalysisOptions FromPreferenceValues(
        int minimumIncreasePercent,
        int minimumIncreaseMegabytes)
    {
        return FromAnalysisOptions(new MemorySpikeAnalysisOptions(
            minimumIncreasePercent,
            minimumIncreaseMegabytes));
    }

    public static MemorySpikeTelemetryAnalysisOptions FromAnalysisOptions(
        MemorySpikeAnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MemorySpikeTelemetryAnalysisOptions(
            options.MinimumIncreaseMegabytes * BytesPerMegabyte,
            options.MinimumIncreasePercent / 100d);
    }
}
