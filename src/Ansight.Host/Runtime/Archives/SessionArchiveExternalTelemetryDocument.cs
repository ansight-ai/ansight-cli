namespace Ansight.Host.Runtime.Archives;

internal sealed class SessionArchiveExternalTelemetryDocument
{
    public required IReadOnlyList<SessionMetricChannel> Channels { get; init; }

    public required IReadOnlyList<SessionMetricSample> Metrics { get; init; }
}
