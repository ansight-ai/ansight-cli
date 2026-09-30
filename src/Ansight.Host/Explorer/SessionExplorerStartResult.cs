namespace Ansight.Host.Replay;

public sealed record SessionExplorerStartResult(
    bool IsSuccess,
    string Message,
    Uri? ExplorerUrl,
    int? Port,
    bool WasAlreadyRunning);
