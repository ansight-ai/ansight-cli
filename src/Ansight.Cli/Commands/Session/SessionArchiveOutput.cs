namespace Ansight.Cli.Commands.Session;

internal sealed record SessionArchiveOutput(
    string Schema,
    string Operation,
    bool IsSuccess,
    string Message,
    string? SessionId,
    string Path);
