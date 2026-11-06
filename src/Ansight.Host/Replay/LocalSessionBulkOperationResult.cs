
namespace Ansight.Host.Replay;

public sealed record LocalSessionBulkOperationResult(
    bool IsSuccess,
    string Message,
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<LocalSessionOperationItem> Items);
