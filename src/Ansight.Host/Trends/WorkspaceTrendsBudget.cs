namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsBudget(
    double? GreaterThanOrEqual,
    double? LessThanOrEqual,
    double? AbsoluteLessThanOrEqual);
