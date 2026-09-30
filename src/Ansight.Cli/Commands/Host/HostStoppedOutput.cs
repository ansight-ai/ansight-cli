namespace Ansight.Cli.Commands.Host;

internal sealed record HostStoppedOutput(string Schema, int ProcessId, bool IsStopped);
