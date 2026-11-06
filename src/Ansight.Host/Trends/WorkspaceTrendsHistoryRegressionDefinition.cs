namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryRegressionDefinition(
    double? RelativeIncreasePercent,
    double? RelativeDecreasePercent,
    double? AbsoluteIncrease,
    double? AbsoluteDecrease,
    int ConsecutiveRuns)
{
    public bool RequireAllThresholds { get; init; } = true;
}
