namespace Ansight.Host.Replay;

public sealed record SessionReplayStartResult(
    bool IsSuccess,
    string Message,
    string SessionId,
    Uri? ReplayUrl,
    int? Port,
    bool WasAlreadyRunning);
