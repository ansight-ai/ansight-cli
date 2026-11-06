namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsCatalogResult(
    string WorkspacePath,
    IReadOnlyList<WorkspaceTrendsDefinition> Definitions,
    IReadOnlyList<WorkspaceTrendsHistoryDefinition> HistoryDefinitions,
    IReadOnlyList<string> Warnings)
{
    public WorkspaceTrendsDefinition? FindDefinition(string trendsId) => Definitions.FirstOrDefault(
        definition => string.Equals(
            definition.TrendsId,
            trendsId,
            StringComparison.OrdinalIgnoreCase));
}
