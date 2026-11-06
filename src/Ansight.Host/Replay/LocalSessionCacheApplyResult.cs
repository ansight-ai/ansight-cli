
namespace Ansight.Host.Replay;

public sealed record LocalSessionCacheApplyResult(
    bool IsSuccess,
    string Message,
    int DeletedCount,
    int FailedCount,
    IReadOnlyList<LocalSessionOperationItem> Items);
