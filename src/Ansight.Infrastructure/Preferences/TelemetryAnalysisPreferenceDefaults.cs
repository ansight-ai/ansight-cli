namespace Ansight.Infrastructure.Preferences;

public static class TelemetryAnalysisPreferenceDefaults
{
    private const long BytesPerMegabyte = 1024L * 1024L;

    public const int MemorySpikeMinimumIncreasePercent = 15;
    public const int MinimumMemorySpikeMinimumIncreasePercent = 1;
    public const int MaximumMemorySpikeMinimumIncreasePercent = 100;

    public const int MemorySpikeMinimumIncreaseMegabytes = 128;
    public const int MinimumMemorySpikeMinimumIncreaseMegabytes = 1;
    public const int MaximumMemorySpikeMinimumIncreaseMegabytes = 4096;

    public static int NormalizeMemorySpikeMinimumIncreasePercent(int minimumIncreasePercent)
    {
        return Math.Clamp(
            minimumIncreasePercent,
            MinimumMemorySpikeMinimumIncreasePercent,
            MaximumMemorySpikeMinimumIncreasePercent);
    }

    public static int NormalizeMemorySpikeMinimumIncreaseMegabytes(int minimumIncreaseMegabytes)
    {
        return Math.Clamp(
            minimumIncreaseMegabytes,
            MinimumMemorySpikeMinimumIncreaseMegabytes,
            MaximumMemorySpikeMinimumIncreaseMegabytes);
    }

    public static double ToMemorySpikeMinimumIncreaseRatio(int minimumIncreasePercent)
    {
        return NormalizeMemorySpikeMinimumIncreasePercent(minimumIncreasePercent) / 100d;
    }

    public static long ToMemorySpikeMinimumIncreaseBytes(int minimumIncreaseMegabytes)
    {
        return NormalizeMemorySpikeMinimumIncreaseMegabytes(minimumIncreaseMegabytes) * BytesPerMegabyte;
    }
}
