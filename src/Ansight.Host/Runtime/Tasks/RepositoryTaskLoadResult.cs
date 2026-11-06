using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskLoadResult(
    IReadOnlyList<RepositoryTaskDefinition> Tasks,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<RepositoryTaskSupportModule> SupportModules { get; init; } = [];
}
