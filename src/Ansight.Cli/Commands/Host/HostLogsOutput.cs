namespace Ansight.Cli.Commands.Host;

internal sealed record HostLogsOutput(
    string Schema,
    bool IsHostRunning,
    string DataDirectory,
    string LogDirectory,
    string? CurrentLogFilePath);
