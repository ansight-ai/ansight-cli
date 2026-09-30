using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class GetVisualTreeSnapshotTool : Operation
{
    public GetVisualTreeSnapshotTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_get_visual_tree_snapshot";

    protected override string Title => "Get Visual Tree Snapshot";

    protected override string Description => "Return the full payload for a captured visual tree snapshot by snapshot id or nearest timestamp.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific session id to inspect.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one matching session exists.", nullable: true),
            ["includeHistorical"] = ToolSchema.Boolean("When resolving by appId, include historical sessions. Defaults to true.", nullable: true),
            ["snapshotId"] = ToolSchema.String("Optional visual tree snapshot id.", nullable: true),
            ["timestampUtc"] = ToolSchema.String("Optional timestamp in ISO-8601 UTC; the nearest snapshot is used.", nullable: true, format: "date-time"),
            ["maxDepth"] = ToolSchema.Integer("Optional maximum root depth to include. Omit for full payload.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !ArgumentReader.TryReadDateTimeOffsetArgument(arguments, "timestampUtc", out var timestampUtc, out errorMessage)
            || !TouchReviewArgumentReader.TryReadOptionalIntegerArgument(arguments, "maxDepth", out var maxDepth, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid visual tree snapshot arguments."));
        }

        var snapshotId = NormalizeOptionalString(arguments?["snapshotId"]?.GetValue<string>());
        var visualTree = ResolveVisualTree(snapshot!, snapshotId, timestampUtc?.ToUniversalTime());
        if (visualTree is null)
        {
            return Task.FromResult(ToolError($"Session '{snapshot!.SessionId}' has no matching visual tree snapshot."));
        }

        var payload = PayloadJson.BuildSessionVisualTreeSnapshotPayload(snapshot!, visualTree);
        if (payload["payload"] is JsonObject visualTreePayload)
        {
            payload["payload"] = VisualTreePayloadPruner.Prune(visualTreePayload, maxDepth);
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot!, SessionReviewContext.IsLiveSession(sessionResolver, snapshot!)),
                ["targetSelector"] = snapshotId is not null ? "snapshotId" : timestampUtc.HasValue ? "timestampUtc" : "latest",
                ["visualTree"] = payload
            },
            isError: false));
    }

    private static SessionVisualTreeSnapshot? ResolveVisualTree(
        AppSessionSnapshot snapshot,
        string? snapshotId,
        DateTimeOffset? timestampUtc)
    {
        if (snapshotId is not null)
        {
            return snapshot.VisualTreeSnapshots.FirstOrDefault(candidate =>
                string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal));
        }

        if (timestampUtc.HasValue)
        {
            return snapshot.VisualTreeSnapshots
                .OrderBy(candidate => (candidate.CapturedAtUtc - timestampUtc.Value).Duration())
                .ThenBy(candidate => candidate.CapturedAtUtc)
                .FirstOrDefault();
        }

        return snapshot.VisualTreeSnapshots
            .OrderByDescending(candidate => candidate.CapturedAtUtc)
            .ThenBy(candidate => candidate.SnapshotId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
