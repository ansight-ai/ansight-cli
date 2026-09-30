using Ansight.Host;

namespace Ansight.Cli.Commands.Tasks;

internal sealed record RepositoryTaskCatalogOutput(
    string Schema,
    RepositoryTaskCatalog Catalog);
