namespace Ansight.Host.SimulatorAgent;

public sealed record SimulatorAgentAccessibilityTraceEvidence(
    string Source,
    DateTimeOffset CapturedAtUtc,
    int NodeCount,
    string? SnapshotId,
    SimulatorAgentAuditPayload Snapshot);
