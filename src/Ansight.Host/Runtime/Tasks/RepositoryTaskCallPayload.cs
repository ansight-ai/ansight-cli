using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskCallPayload(
    string Content,
    int OriginalCharacterCount,
    bool WasTruncated,
    string Sha256);
