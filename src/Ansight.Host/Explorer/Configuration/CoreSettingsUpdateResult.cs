namespace Ansight.Host.Replay;

public sealed record CoreSettingsUpdateResult(
    bool IsSuccess,
    string Message,
    CoreSettings Settings);
