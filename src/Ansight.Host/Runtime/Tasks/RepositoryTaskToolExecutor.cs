using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal delegate Task<global::Ansight.Host.Runtime.Operations.RequestResult> RepositoryTaskToolExecutor(
    string toolName,
    JsonObject arguments,
    string? correlationId);
