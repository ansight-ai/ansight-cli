using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Ansight.Host.Runtime.Tasks;

public sealed record RepositoryTaskSourceModule(
    string Path,
    string Language,
    string Content,
    string Sha256,
    int OriginalCharacterCount,
    bool WasTruncated);
