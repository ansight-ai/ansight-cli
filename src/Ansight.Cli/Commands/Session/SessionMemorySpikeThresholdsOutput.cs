namespace Ansight.Cli.Commands.Session;

internal sealed record SessionMemorySpikeThresholdsOutput(
    int MinimumIncreasePercent,
    int MinimumIncreaseMegabytes);
