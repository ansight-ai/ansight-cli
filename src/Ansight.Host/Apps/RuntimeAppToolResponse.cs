using Ansight.Tools;

namespace Ansight.Host.Apps;

public sealed record RuntimeAppToolResponse(
    bool Success,
    string Message,
    ToolProtocolEnvelope? Envelope,
    string? ArtifactSnapshotId = null);
