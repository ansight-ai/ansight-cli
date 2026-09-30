namespace Ansight.Host.Runtime.WebSocketSessions;

internal sealed record BinaryToolArtifactTransferMetrics(
    string TransferId,
    long DurationMs,
    long SizeBytes,
    string? SnapshotId = null);
