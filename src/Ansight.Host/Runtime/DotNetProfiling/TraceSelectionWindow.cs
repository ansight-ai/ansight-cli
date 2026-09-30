namespace Ansight.Host.Runtime.DotNetProfiling;

internal sealed record TraceSelectionWindow(
    double? StartMilliseconds,
    double? EndMilliseconds,
    int? ProcessId,
    int? ThreadId)
{
    public static TraceSelectionWindow EntireTrace { get; } = new(null, null, null, null);

    public bool Includes(double timestampMilliseconds, int processId, int threadId)
    {
        return (StartMilliseconds is null || timestampMilliseconds >= StartMilliseconds.Value)
               && (EndMilliseconds is null || timestampMilliseconds <= EndMilliseconds.Value)
               && (ProcessId is null || processId == ProcessId.Value)
               && (ThreadId is null || threadId == ThreadId.Value);
    }
}
