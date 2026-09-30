namespace Ansight.Cli;

internal sealed record CliError(string Code, string Message, int ExitCode);
