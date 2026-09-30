using System.Text;
using System.Text.Json.Nodes;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.SessionEvidence;

internal sealed class ReadArtifactFileTool : Operation
{
    public ReadArtifactFileTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_read_artifact_file";

    protected override string Title => "Read Artifact File";

    protected override string Description => "Read a file captured in a session artifact snapshot, returning text or base64 content with truncation metadata.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: ReadProperties(),
        required: ["path"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!SessionReviewContext.TryResolveReviewSession(sessionResolver, arguments, out var snapshot, out var errorMessage)
            || !ArgumentReader.TryReadPositiveLimit(arguments, "maxBytes", SessionEvidenceDefaults.DefaultArtifactReadBytes, SessionEvidenceDefaults.MaxArtifactReadBytes, out var maxBytes, out errorMessage)
            || !TouchReviewArgumentReader.TryReadBooleanArgument(arguments, "forceBase64", defaultValue: false, out var forceBase64, out errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage ?? "Invalid artifact read arguments."));
        }

        var resolvedSnapshot = snapshot!;
        var requestedPath = SessionFileLocator.NormalizeArtifactPath(arguments?["path"]?.GetValue<string>());
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return Task.FromResult(ToolError("path is required."));
        }

        var snapshotId = NormalizeOptionalString(arguments?["snapshotId"]?.GetValue<string>());
        if (!TryFindEntry(resolvedSnapshot, snapshotId, requestedPath, out var artifactSnapshot, out var entry))
        {
            return Task.FromResult(ToolError($"Artifact file '{requestedPath}' was not found."));
        }

        var resolvedArtifactSnapshot = artifactSnapshot!;
        var resolvedEntry = entry!;
        if (SessionEvidencePayloads.IsArtifactDirectory(resolvedEntry))
        {
            return Task.FromResult(ToolError($"Artifact path '{requestedPath}' is a directory."));
        }

        if (!SessionFileLocator.TryResolveArtifactEntryPath(applicationPaths, resolvedSnapshot, resolvedArtifactSnapshot, resolvedEntry, out var filePath)
            || !File.Exists(filePath))
        {
            return Task.FromResult(ToolError($"Artifact file '{requestedPath}' metadata exists, but the local file was not found."));
        }

        var bytes = File.ReadAllBytes(filePath);
        var returnedBytes = bytes.Length > maxBytes ? bytes.Take(maxBytes).ToArray() : bytes;
        var isText = !forceBase64 && LooksTextual(resolvedEntry.MimeType, resolvedEntry.FileExtension, returnedBytes);
        var payload = new JsonObject
        {
            ["session"] = SessionReviewContext.BuildSessionHeaderPayload(resolvedSnapshot, SessionReviewContext.IsLiveSession(sessionResolver, resolvedSnapshot)),
            ["snapshot"] = SessionEvidencePayloads.BuildArtifactSnapshotPayload(resolvedArtifactSnapshot),
            ["entry"] = SessionEvidencePayloads.BuildArtifactEntryPayload(resolvedArtifactSnapshot, resolvedEntry, filePath),
            ["byteCount"] = bytes.Length,
            ["returnedByteCount"] = returnedBytes.Length,
            ["isTruncated"] = returnedBytes.Length < bytes.Length,
            ["encoding"] = isText ? "utf-8" : "base64"
        };

        if (isText)
        {
            payload["text"] = Encoding.UTF8.GetString(returnedBytes);
        }
        else
        {
            payload["base64"] = Convert.ToBase64String(returnedBytes);
        }

        return Task.FromResult(RequestResult.ToolResult(payload, isError: false));
    }

    private static Dictionary<string, ToolSchema> ReadProperties()
    {
        var properties = SessionReviewToolSchemas.ReviewSessionProperties();
        properties["snapshotId"] = ToolSchema.String("Optional artifact snapshot id. Required when multiple snapshots contain the same path.", nullable: true);
        properties["path"] = ToolSchema.String("SnapshotRelativePath or ArchiveRelativePath of the artifact file.");
        properties["maxBytes"] = ToolSchema.Integer("Maximum bytes to return. Defaults to 65536, max 1048576.", nullable: true);
        properties["forceBase64"] = ToolSchema.Boolean("Return base64 even when the file appears textual. Defaults to false.", nullable: true);
        return properties;
    }

    private static bool TryFindEntry(
        AppSessionSnapshot snapshot,
        string? snapshotId,
        string requestedPath,
        out SessionArtifactSnapshot? artifactSnapshot,
        out SessionArtifactEntry? entry)
    {
        artifactSnapshot = null;
        entry = null;
        foreach (var candidateSnapshot in snapshot.ArtifactSnapshots.Where(candidate =>
                     snapshotId is null || string.Equals(candidate.SnapshotId, snapshotId, StringComparison.Ordinal)))
        {
            var candidateEntry = candidateSnapshot.Entries.FirstOrDefault(candidate =>
                string.Equals(SessionFileLocator.NormalizeArtifactPath(candidate.SnapshotRelativePath), requestedPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(SessionFileLocator.NormalizeArtifactPath(candidate.ArchiveRelativePath), requestedPath, StringComparison.OrdinalIgnoreCase));
            if (candidateEntry is null)
            {
                continue;
            }

            artifactSnapshot = candidateSnapshot;
            entry = candidateEntry;
            return true;
        }

        return false;
    }

    private static bool LooksTextual(string? mimeType, string? extension, IReadOnlyList<byte> bytes)
    {
        if (!string.IsNullOrWhiteSpace(mimeType)
            && (mimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("xml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (extension is not null
            && extension.Trim().TrimStart('.').ToLowerInvariant() is "txt" or "json" or "xml" or "csv" or "md" or "log" or "yaml" or "yml")
        {
            return true;
        }

        return bytes.Count > 0 && bytes.Take(Math.Min(bytes.Count, 512)).All(value => value is 9 or 10 or 13 || value >= 32);
    }
}
