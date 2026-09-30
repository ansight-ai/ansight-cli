namespace Ansight.Cli.Commands.Update;

internal sealed record CliUpdateProgress(
    string Message,
    bool IsRawInstallerOutput = false);
