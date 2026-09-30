
namespace Ansight.Host.Replay;

public sealed record LocalSessionBulkOperationRequest(
    IReadOnlyList<string> SessionIds,
    string Operation);
