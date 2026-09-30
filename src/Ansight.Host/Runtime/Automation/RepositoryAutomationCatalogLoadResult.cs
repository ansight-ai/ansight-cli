namespace Ansight.Host.Runtime.Automation;

internal sealed record RepositoryAutomationCatalogLoadResult(
    RepositoryAutomationCatalog Catalog,
    IReadOnlyList<string> Warnings);
