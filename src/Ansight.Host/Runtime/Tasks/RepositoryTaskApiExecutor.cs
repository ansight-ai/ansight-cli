using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal delegate Task<RepositoryTaskApiResult> RepositoryTaskApiExecutor(
    RepositoryTaskExecutionRequest parentRequest,
    string methodName,
    JsonObject arguments,
    string? correlationId,
    CancellationToken cancellationToken);
