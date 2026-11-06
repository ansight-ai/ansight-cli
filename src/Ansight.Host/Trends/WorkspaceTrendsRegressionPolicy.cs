namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsRegressionPolicy(
    WorkspaceTrendsRegressionDirection Direction,
    double? Percent,
    double? Absolute,
    int ConfirmRuns,
    int BaselineRuns,
    int MinimumBaselineRuns,
    IReadOnlyList<string> SeriesBy,
    bool Blocking);
