using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskSourceTrace(
    string TaskId,
    IReadOnlyList<RepositoryTaskSourceModule> Modules,
    string? CaptureError = null);
