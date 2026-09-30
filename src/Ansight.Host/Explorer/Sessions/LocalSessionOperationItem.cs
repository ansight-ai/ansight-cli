
namespace Ansight.Host.Replay;

public sealed record LocalSessionOperationItem(
    string SessionId,
    bool IsSuccess,
    string Message);
