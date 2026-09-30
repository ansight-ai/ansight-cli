
namespace Ansight.Host.Replay;

public sealed record LocalSessionMetadataUpdateRequest(
    bool IsPinned,
    IReadOnlyList<string>? Tags,
    string? Notes,
    string? Name);
