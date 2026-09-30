namespace Ansight.Cli.HostConnection;

internal sealed record ControlResponse(
    string Schema,
    int ExitCode,
    string StandardOutput,
    string StandardError);
