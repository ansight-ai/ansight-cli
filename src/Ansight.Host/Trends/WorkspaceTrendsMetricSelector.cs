namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsMetricSelector(
    string? Type,
    string? Name,
    string? Source,
    string? Kind,
    bool RequireExactlyOne);
