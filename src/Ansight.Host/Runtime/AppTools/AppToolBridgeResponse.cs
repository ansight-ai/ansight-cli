namespace Ansight.Host.Runtime.AppTools;

using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

internal sealed record AppToolBridgeResponse(
    bool Success,
    string Message,
    ToolProtocolEnvelope? Envelope,
    bool RequiresCatalogQuery,
    string? ArtifactSnapshotId = null)
{
    public string? FailureCode { get; init; }

    public static AppToolBridgeResponse FromUnavailable(string message)
        => FromFailure(message) with { FailureCode = ExecutionCapabilities.ErrorCode };

    public static AppToolBridgeResponse FromSuccess(
        string message,
        ToolProtocolEnvelope envelope,
        string? artifactSnapshotId = null)
        => new(true, message, envelope, false, artifactSnapshotId);

    public static AppToolBridgeResponse FromFailure(string message, bool requiresCatalogQuery = false)
        => new(false, message, null, requiresCatalogQuery, null);
}
