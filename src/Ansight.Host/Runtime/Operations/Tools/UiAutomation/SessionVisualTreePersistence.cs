using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

internal static class SessionVisualTreePersistence
{
    public static PersistedVisualTreeResult Persist(
        ISessionIngestion sessionIngestion,
        AppSessionSnapshot session,
        string toolId,
        JsonObject payload,
        ActionEvidenceMetadata? action = null,
        PersistedScreenshotEvidence? screenshot = null)
    {
        ArgumentNullException.ThrowIfNull(sessionIngestion);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(payload);

        var capturedAtUtc = ReadCapturedAtUtc(payload);
        var treeHash = ComputeHash(payload);
        var typeRegistry = VisualTreeTypeRegistry.FromPayload(payload);
        var nodeCount = ReadInteger(payload, "nodeCount")
                        ?? (payload["root"] is JsonObject root
                            ? (int?)LiveUiNodeQuery.Enumerate(root, typeRegistry).Count()
                            : null)
                        ?? 0;
        var snapshotId = action is null
            ? $"ansight-live-tree-{capturedAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"
            : $"{action.ActionId}-{action.Phase}-{Guid.NewGuid():N}";
        var reportedKind = ReadString(payload, "treeKind");
        var source = ReadString(payload, "source");
        var format = ReadString(payload, "format");
        var snapshot = new SessionVisualTreeSnapshot
        {
            SnapshotId = snapshotId,
            CapturedAtUtc = capturedAtUtc,
            VisualTreeKind = VisualTreeContract.NormalizeKind(toolId, reportedKind, source, format),
            VisualTreeFormat = VisualTreeContract.NormalizeFormat(format),
            RuntimePlatform = VisualTreeContract.NormalizeRuntimePlatform(ReadString(payload, "platform")),
            Source = source ?? toolId,
            RootScope = VisualTreeContract.NormalizeRootScope(ReadString(payload, "rootScope"), toolId),
            MaxDepth = ReadInteger(payload, "maxDepth") ?? 0,
            IncludeProperties = ReadBoolean(payload, "includeProperties"),
            IncludeBindableProperties = ReadBoolean(payload, "includeBindableProperties"),
            NodeCount = nodeCount,
            Truncated = ReadBoolean(payload, "truncated"),
            ScreenshotFrameId = screenshot?.Frame.FrameId,
            ScreenshotCapturedAtUtc = screenshot?.Frame.CapturedAtUtc,
            ActionId = action?.ActionId,
            ActionCapability = action?.Capability,
            EvidencePhase = action?.Phase,
            TreeHash = treeHash,
            ScreenshotHash = screenshot?.Hash,
            Payload = payload.DeepClone() as JsonObject ?? new JsonObject()
        };
        var result = sessionIngestion.AddSessionVisualTreeSnapshot(session.SessionId, snapshot);
        return result.IsSuccess
            ? new PersistedVisualTreeResult(true, snapshot, result.Message)
            : new PersistedVisualTreeResult(false, null, result.Message);
    }

    public static string ComputeHash(JsonNode payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DateTimeOffset ReadCapturedAtUtc(JsonObject payload)
        => DateTimeOffset.TryParse(ReadString(payload, "capturedAtUtc"), out var capturedAtUtc)
            ? capturedAtUtc.ToUniversalTime()
            : DateTimeOffset.UtcNow;

    private static string? ReadString(JsonObject payload, string propertyName)
        => LiveUiNodeQuery.ReadString(payload, propertyName);

    private static int? ReadInteger(JsonObject payload, string propertyName)
        => payload[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;

    private static bool ReadBoolean(JsonObject payload, string propertyName)
        => payload[propertyName] is JsonValue value
           && value.TryGetValue<bool>(out var result)
           && result;
}

internal sealed record ActionEvidenceMetadata(
    string ActionId,
    string Capability,
    string Phase);

internal sealed record PersistedScreenshotEvidence(
    SessionImageFrame Frame,
    string Hash,
    string ArtifactPath);

internal sealed record PersistedVisualTreeResult(
    bool IsSuccess,
    SessionVisualTreeSnapshot? Snapshot,
    string Message);
