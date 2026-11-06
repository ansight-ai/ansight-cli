using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Tasks;

internal sealed record RepositoryTaskApiResult(
    global::Ansight.Host.Runtime.Operations.RequestResult Result,
    IReadOnlyList<RepositoryTaskToolCall>? ChildCalls = null,
    RepositoryTaskSourceTrace? SourceTrace = null,
    IReadOnlyList<RepositoryTaskAssertion>? Assertions = null);
