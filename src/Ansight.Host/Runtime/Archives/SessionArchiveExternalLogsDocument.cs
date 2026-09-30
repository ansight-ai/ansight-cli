namespace Ansight.Host.Runtime.Archives;

internal sealed class SessionArchiveExternalLogsDocument
{
    public required IReadOnlyList<LogEntry> Logs { get; init; }
}
