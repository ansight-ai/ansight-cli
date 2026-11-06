using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskSupportModule(
    string RelativePath,
    string Source,
    bool IsDeclaration);
