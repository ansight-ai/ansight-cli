namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsChartDefinition(
    string? Title,
    WorkspaceTrendsChartAxisDefinition YAxis);
