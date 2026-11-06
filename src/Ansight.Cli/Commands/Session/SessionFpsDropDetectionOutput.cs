namespace Ansight.Cli.Commands.Session;

internal sealed record SessionFpsDropDetectionOutput(
    byte ChannelId,
    string ChannelName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset FocusUtc,
    DateTimeOffset BaselineCapturedAtUtc,
    double BaselineFps,
    double MinimumFps,
    double DropFps,
    string Severity,
    double SeverityScore);
