using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ListArtifactFilesTool : Operation
{
    public ListArtifactFilesTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_artifact_files";

    protected override string Title => "List Artifact Files";

    protected override string Description => "List files and directories captured in session artifact snapshots.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ListProperties(),
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "limit", SessionEvidenceDefaults.DefaultResultLimit, SessionEvidenceDefaults.MaxResultLimit, out var limit, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid artifact listing arguments."));
        }

        var snapshotId = NormalizeOptionalString(arguments?["snapshotId"]?.GetValue<string>());
        var pathPrefix = SessionFileLocator.NormalizeArtifactPath(arguments?["pathPrefix"]?.GetValue<string>());
        var entries = new List<JsonNode?>();
        foreach (var artifactSnapshot in snapshot!.ArtifactSnapshots
                     .Where(candidate => snapshotId is null || string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal))
                     .OrderBy(candidate => candidate.CapturedAtUtc)
                     .ThenBy(candidate => candidate.SnapshotId, StringComparer.Ordinal))
        {
            foreach (var entry in artifactSnapshot.Entries
                         .Where(entry => string.IsNullOrWhiteSpace(pathPrefix)
                                         || SessionFileLocator.NormalizeArtifactPath(entry.SnapshotRelativePath).StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)
                                         || SessionFileLocator.NormalizeArtifactPath(entry.ArchiveRelativePath).StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                SessionFileLocator.TryResolveArtifactEntryPath(applicationPaths, snapshot, artifactSnapshot, entry, out var localPath);
                entries.Add(SessionEvidencePayloads.BuildArtifactEntryPayload(artifactSnapshot, entry, localPath));
                if (entries.Count >= limit)
                {
                    break;
                }
            }

            if (entries.Count >= limit)
            {
                break;
            }
        }

        return Task.FromResult(RequestResult.ToolResult(
            new JsonObject
            {
                ["session"] = SessionReviewContext.BuildSessionHeaderPayload(snapshot, SessionReviewContext.IsLiveSession(sessionResolver, snapshot)),
                ["artifactSnapshotCount"] = snapshot.ArtifactSnapshots.Count,
                ["returnedEntryCount"] = entries.Count,
                ["isTruncated"] = entries.Count >= limit,
                ["entries"] = PayloadJson.CreateJsonArray(entries)
            },
            isError: false));
    }

    private static Dictionary<string, ToolSchema> ListProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["snapshotId"] = ToolSchema.String("Optional artifact snapshot id to list.", nullable: true);
        properties["pathPrefix"] = ToolSchema.String("Optional snapshot or archive relative path prefix.", nullable: true);
        properties["limit"] = ToolSchema.Integer("Maximum number of entries to return. Defaults to 200, max 5000.", nullable: true);
        return properties;
    }
}
