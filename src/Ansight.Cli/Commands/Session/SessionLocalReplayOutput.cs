namespace Ansight.Cli.Commands.Session;

internal sealed record SessionLocalReplayOutput(
    string Schema,
    bool IsSuccess,
    string Message,
    string SessionId,
    string? ReplayUrl,
    int? Port,
    bool WasAlreadyRunning,
    bool IsResidentHost);
