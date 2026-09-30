using System.Globalization;

namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentTransportAttempt(
    string Mode,
    string? ReplayReason,
    int InputItemCount,
    int RequestBytes,
    long DurationMilliseconds)
{
    public DateTimeOffset? StartedUtc { get; init; }

    public bool? ConnectionSucceeded { get; init; }

    public long? ConnectionDurationMilliseconds { get; init; }

    public long? RequestPreparedMilliseconds { get; init; }
    public long? RequestSentMilliseconds { get; init; }
    public long? FirstResponseMilliseconds { get; init; }
    public long? ResponseCompletedMilliseconds { get; init; }
    public long? ParsingDurationMilliseconds { get; init; }

    public string? Error { get; init; }
}
