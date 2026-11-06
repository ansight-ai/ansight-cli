namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsChartAxisDefinition(
    string? Unit,
    double? Scale,
    int? FractionDigits,
    double? Minimum,
    double? Maximum,
    bool? IncludeZero);
