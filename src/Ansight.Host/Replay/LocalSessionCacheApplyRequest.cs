
namespace Ansight.Host.Replay;

public sealed record LocalSessionCacheApplyRequest(
    IReadOnlyList<string> SessionIds);
