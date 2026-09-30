using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionLogsOutput(
    string Schema,
    string SessionId,
    int TotalLogCount,
    IReadOnlyList<LogEntry> Logs);
