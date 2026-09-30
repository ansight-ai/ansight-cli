namespace Ansight.Infrastructure.Preferences;

public static class SessionCleanupPreferenceDefaults
{
    private const long Mebibyte = 1024L * 1024L;
    private const long Gibibyte = 1024L * Mebibyte;

    public const bool Enabled = true;
    public const int RetentionDays = 90;
    public const int CompactionAgeDays = 30;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 365;
    public const long MaximumCacheBytes = 5L * Gibibyte;
    public const long MinimumMaximumCacheBytes = 256L * Mebibyte;
    public const long MaximumMaximumCacheBytes = 1024L * Gibibyte;

    public static int NormalizeRetentionDays(int retentionDays)
    {
        return Math.Clamp(retentionDays, MinimumRetentionDays, MaximumRetentionDays);
    }

    public static int NormalizeCompactionAgeDays(int compactionAgeDays)
    {
        return Math.Clamp(compactionAgeDays, MinimumRetentionDays, MaximumRetentionDays);
    }

    public static long NormalizeMaximumCacheBytes(long maximumCacheBytes)
    {
        return Math.Clamp(maximumCacheBytes, MinimumMaximumCacheBytes, MaximumMaximumCacheBytes);
    }
}
