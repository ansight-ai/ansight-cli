namespace Ansight.Host.Telemetry;

public sealed record MemorySpikeAnalysisOptions(
    int MinimumIncreasePercent = 15,
    int MinimumIncreaseMegabytes = 128)
{
    public const int DefaultMinimumIncreasePercent = 15;
    public const int MinimumAllowedIncreasePercent = 1;
    public const int MaximumAllowedIncreasePercent = 100;

    public const int DefaultMinimumIncreaseMegabytes = 128;
    public const int MinimumAllowedIncreaseMegabytes = 1;
    public const int MaximumAllowedIncreaseMegabytes = 4096;

    public static MemorySpikeAnalysisOptions Default { get; } = new();
}
