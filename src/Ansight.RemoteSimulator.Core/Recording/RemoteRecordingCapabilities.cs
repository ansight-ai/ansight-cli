namespace Ansight.RemoteSimulator.Core.Recording;

public sealed record RemoteRecordingCapabilities(
    bool HasScreenReplay,
    bool HasLogs,
    bool HasAnnotations,
    bool HasTelemetry);
