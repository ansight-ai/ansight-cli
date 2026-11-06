namespace Ansight.Cli.Commands.Session;

internal sealed record SessionMemorySpikeDetectionOutput(
    byte ChannelId,
    string ChannelName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset FocusUtc,
    DateTimeOffset BaselineCapturedAtUtc,
    double BaselineBytes,
    double PeakBytes,
    double DeltaBytes,
    double DeltaPercent,
    DateTimeOffset? RetainedUntilUtc,
    string Severity,
    double SeverityScore);
