namespace Ansight.Cli.Tests.TestSupport;

internal sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
