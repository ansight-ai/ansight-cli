namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsChartMetadata(
    string DecisionId,
    string MetricKey,
    string? Title,
    WorkspaceTrendsChartAxisDefinition YAxis)
{
    public string DefinitionHash { get; init; } = string.Empty;
}
