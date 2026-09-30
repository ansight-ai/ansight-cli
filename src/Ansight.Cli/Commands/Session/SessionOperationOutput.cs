namespace Ansight.Cli.Commands.Session;

internal sealed record SessionOperationOutput(
    string Schema,
    string Operation,
    string SessionId,
    bool IsSuccess,
    string Message);
