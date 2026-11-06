namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsDisplayDefinition(
    string? Title,
    string? Unit,
    double? Scale,
    int? FractionDigits,
    double? Minimum,
    double? Maximum,
    bool? IncludeZero);
