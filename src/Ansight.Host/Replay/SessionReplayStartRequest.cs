namespace Ansight.Host.Replay;

public sealed record SessionReplayStartRequest(
    string SessionId,
    int Port = 0);
