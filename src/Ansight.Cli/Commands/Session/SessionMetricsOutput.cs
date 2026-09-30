using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionMetricsOutput(
    string Schema,
    string SessionId,
    int TotalMetricSampleCount,
    IReadOnlyList<SessionMetricChannel> Channels,
    IReadOnlyList<SessionMetricSample> Samples);
