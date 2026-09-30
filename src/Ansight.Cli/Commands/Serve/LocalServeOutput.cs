namespace Ansight.Cli.Commands.Serve;

internal sealed record LocalServeOutput(
    string Schema,
    bool IsSuccess,
    string Message,
    string? ExplorerUrl,
    int? Port,
    bool WasAlreadyRunning,
    bool IsResidentHost);
