namespace Ansight.Cli.Commands.Session;

internal sealed record SessionCacheFailureOutput(
    string SessionId,
    string Message);
