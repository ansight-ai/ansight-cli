namespace Ansight.Cli.Commands.App;

internal sealed record AppToolOutput(
    string Schema,
    string Operation,
    string SessionId,
    string? ToolId,
    bool IsSuccess,
    string Message,
    object? Envelope);
