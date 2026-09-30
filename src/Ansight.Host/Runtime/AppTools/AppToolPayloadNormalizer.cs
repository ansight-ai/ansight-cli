using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.AppTools;

internal static class AppToolPayloadNormalizer
{
    private const string ArtifactRequestToolId = "artifacts.request";
    private const string UiGetScreenshotToolId = "ui.get_screenshot";
    private const int DefaultReactComponentTreeMaxDepth = 28;

    public static bool IsVisualTreeTool(string toolId)
        => string.Equals(toolId, VisualTreeContract.MauiToolId, StringComparison.Ordinal)
           || string.Equals(toolId, VisualTreeContract.ReactShadowToolId, StringComparison.Ordinal)
           || string.Equals(toolId, VisualTreeContract.ReactComponentToolId, StringComparison.Ordinal)
           || string.Equals(toolId, VisualTreeContract.FlutterToolId, StringComparison.Ordinal)
           || string.Equals(toolId, VisualTreeContract.DomToolId, StringComparison.Ordinal)
           || string.Equals(toolId, VisualTreeContract.NativeToolId, StringComparison.Ordinal);

    public static JsonObject ApplyHostToolDefaults(
        string toolId,
        JsonObject? arguments,
        int defaultVisualTreeMaxDepth,
        int defaultScreenshotQuality,
        int defaultScreenshotMaxWidth,
        out JsonObject? appliedDefaults)
    {
        var normalizedArguments = arguments?.DeepClone() as JsonObject ?? new JsonObject();
        appliedDefaults = null;

        static void ApplyDefault(JsonObject target, ref JsonObject? defaults, string key, JsonNode value)
        {
            if (target[key] is not null)
            {
                return;
            }

            target[key] = value.DeepClone();
            defaults ??= new JsonObject();
            defaults[key] = value.DeepClone();
        }

        if (IsVisualTreeTool(toolId))
        {
            var maxDepth = IsReactComponentTreeTool(toolId)
                ? Math.Min(defaultVisualTreeMaxDepth, DefaultReactComponentTreeMaxDepth)
                : defaultVisualTreeMaxDepth;
            ApplyDefault(normalizedArguments, ref appliedDefaults, "maxDepth", JsonValue.Create(maxDepth)!);
        }

        if (string.Equals(toolId, VisualTreeContract.NativeToolId, StringComparison.Ordinal))
        {
            ApplyDefault(normalizedArguments, ref appliedDefaults, "includeComputedStyles", JsonValue.Create(false)!);
        }
        else if (string.Equals(toolId, UiGetScreenshotToolId, StringComparison.Ordinal))
        {
            ApplyDefault(normalizedArguments, ref appliedDefaults, "format", JsonValue.Create("jpeg")!);
            ApplyDefault(normalizedArguments, ref appliedDefaults, "quality", JsonValue.Create(defaultScreenshotQuality)!);
            ApplyDefault(normalizedArguments, ref appliedDefaults, "maxWidth", JsonValue.Create(defaultScreenshotMaxWidth)!);
            ApplyDefault(normalizedArguments, ref appliedDefaults, "afterScreenUpdates", JsonValue.Create(false)!);
        }

        return normalizedArguments;
    }

    private static bool IsReactComponentTreeTool(string toolId)
        => string.Equals(toolId, VisualTreeContract.ReactComponentToolId, StringComparison.Ordinal);

    public static JsonNode? NormalizeCallToolPayload(
        AppSessionSnapshot snapshot,
        string toolId,
        JsonNode? payload,
        JsonArray artifacts)
    {
        var normalizedPayload = payload?.DeepClone();
        if (normalizedPayload is not JsonObject payloadObject)
        {
            return normalizedPayload;
        }

        NormalizeEmbeddedPayload(snapshot, payloadObject, toolId, artifacts);
        return payloadObject;
    }

    public static JsonNode? NormalizeBatchPayload(
        AppSessionSnapshot snapshot,
        JsonNode? payload,
        JsonArray artifacts)
    {
        var normalizedPayload = payload?.DeepClone();
        if (normalizedPayload is not null)
        {
            NormalizeEmbeddedPayload(snapshot, normalizedPayload, defaultToolId: null, artifacts);
        }

        return normalizedPayload;
    }

    private static void NormalizeEmbeddedPayload(
        AppSessionSnapshot snapshot,
        JsonNode node,
        string? defaultToolId,
        JsonArray artifacts)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    NormalizeEmbeddedPayload(snapshot, item, defaultToolId: null, artifacts);
                }
            }
            return;
        }

        if (node is not JsonObject payloadObject)
        {
            return;
        }

        var toolId = payloadObject["toolId"]?.GetValue<string>() ?? defaultToolId;
        if (!string.IsNullOrWhiteSpace(toolId)
            && payloadObject["result"] is JsonObject result)
        {
            NormalizeToolResult(snapshot, toolId, result, artifacts);
        }

        foreach (var property in payloadObject.ToArray())
        {
            if (!string.Equals(property.Key, "result", StringComparison.Ordinal)
                && property.Value is not null)
            {
                NormalizeEmbeddedPayload(snapshot, property.Value, defaultToolId: null, artifacts);
            }
        }
    }

    private static void NormalizeToolResult(
        AppSessionSnapshot snapshot,
        string toolId,
        JsonObject result,
        JsonArray artifacts)
    {
        if (string.Equals(toolId, UiGetScreenshotToolId, StringComparison.Ordinal))
        {
            ExternalizeScreenshotResult(result, artifacts);
        }
        else if (string.Equals(toolId, ArtifactRequestToolId, StringComparison.Ordinal))
        {
            ExternalizeArtifactRequestResult(result, artifacts);
        }
        else if (IsVisualTreeTool(toolId))
        {
            ExternalizeVisualTreeResult(snapshot, toolId, result, artifacts);
        }
    }

    private static void ExternalizeScreenshotResult(
        JsonObject screenshotResult,
        JsonArray artifacts)
    {
        var streamedArtifactPath = screenshotResult["artifactPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(streamedArtifactPath) || !File.Exists(streamedArtifactPath))
        {
            return;
        }

        var streamedMimeType = screenshotResult["mimeType"]?.GetValue<string>() ?? ResolveScreenshotMimeType(screenshotResult);
        screenshotResult["mimeType"] = streamedMimeType;
        screenshotResult["artifactKind"] = "screenshot";

        artifacts.Add(CreateArtifactDescriptor(
            kind: "screenshot",
            path: streamedArtifactPath,
            mimeType: streamedMimeType,
            sizeBytes: new FileInfo(streamedArtifactPath).Length));
    }

    private static void ExternalizeArtifactRequestResult(
        JsonObject artifactResult,
        JsonArray artifacts)
    {
        var streamedArtifactPath = artifactResult["artifactPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(streamedArtifactPath) || !File.Exists(streamedArtifactPath))
        {
            return;
        }

        var artifact = artifactResult["artifact"] as JsonObject;
        var artifactKind = FirstNonEmpty(
            artifact?["kind"]?.GetValue<string>(),
            artifactResult["artifactKind"]?.GetValue<string>(),
            "artifact");
        var mimeType = FirstNonEmpty(
            artifact?["mimeType"]?.GetValue<string>(),
            artifactResult["mimeType"]?.GetValue<string>(),
            "application/octet-stream");
        artifactResult["artifactKind"] = artifactKind;
        artifactResult["mimeType"] = mimeType;

        artifacts.Add(CreateArtifactDescriptor(
            kind: artifactKind,
            path: streamedArtifactPath,
            mimeType: mimeType,
            sizeBytes: new FileInfo(streamedArtifactPath).Length));
    }

    private static string ResolveScreenshotMimeType(JsonObject screenshotResult)
    {
        var format = screenshotResult["format"]?.GetValue<string>() ?? "png";
        return string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static void ExternalizeVisualTreeResult(
        AppSessionSnapshot snapshot,
        string toolId,
        JsonObject visualTreeResult,
        JsonArray artifacts)
    {
        if (visualTreeResult["screenshot"] is JsonObject embeddedScreenshot)
        {
            ExternalizeScreenshotResult(embeddedScreenshot, artifacts);
        }

        var artifactPath = WriteJsonArtifact(
            snapshot,
            suffix: "visual-tree",
            visualTreeResult);

        var reportedKind = visualTreeResult["treeKind"]?.GetValue<string>();
        var source = visualTreeResult["source"]?.GetValue<string>();
        var snapshotId = visualTreeResult["snapshotId"]?.DeepClone();
        var revision = visualTreeResult["revision"]?.DeepClone();
        var nodeIdentity = visualTreeResult["nodeIdentity"]?.DeepClone();
        var format = visualTreeResult["format"]?.GetValue<string>();
        var platform = visualTreeResult["platform"]?.GetValue<string>();
        var capturedAtUtc = visualTreeResult["capturedAtUtc"]?.DeepClone()
                            ?? JsonValue.Create(DateTimeOffset.UtcNow);
        var rootScope = visualTreeResult["rootScope"]?.GetValue<string>();
        var screenshot = visualTreeResult["screenshot"]?.DeepClone();
        var root = visualTreeResult["root"] as JsonObject;
        var rootType = root is null
            ? null
            : VisualTreeTypeRegistry.FromPayload(visualTreeResult).Resolve(root);
        visualTreeResult.Clear();
        visualTreeResult["visualTreeKind"] = VisualTreeContract.NormalizeKind(
            toolId,
            reportedKind,
            source,
            format);
        visualTreeResult["visualTreeFormat"] = VisualTreeContract.NormalizeFormat(format);
        visualTreeResult["runtimePlatform"] = VisualTreeContract.NormalizeRuntimePlatform(platform);
        visualTreeResult["source"] = source;
        visualTreeResult["snapshotId"] = snapshotId;
        visualTreeResult["revision"] = revision;
        visualTreeResult["nodeIdentity"] = nodeIdentity;
        visualTreeResult["capturedAtUtc"] = capturedAtUtc;
        visualTreeResult["rootScope"] = VisualTreeContract.NormalizeRootScope(rootScope, toolId);
        visualTreeResult["artifactPath"] = artifactPath;
        visualTreeResult["artifactKind"] = "visual_tree";
        if (screenshot is not null)
        {
            visualTreeResult["screenshot"] = screenshot;
        }

        if (root is not null)
        {
            visualTreeResult["rootSummary"] = new JsonObject
            {
                ["id"] = root["id"]?.DeepClone(),
                ["type"] = rootType,
                ["label"] = root["label"]?.DeepClone(),
                ["childCount"] = root["childCount"]?.DeepClone()
            };
        }

        artifacts.Add(CreateArtifactDescriptor(
            kind: "visual_tree",
            path: artifactPath,
            mimeType: "application/json",
            sizeBytes: new FileInfo(artifactPath).Length));
    }

    private static JsonObject CreateArtifactDescriptor(
        string kind,
        string path,
        string mimeType,
        long sizeBytes)
    {
        return new JsonObject
        {
            ["kind"] = kind,
            ["path"] = path,
            ["mimeType"] = mimeType,
            ["sizeBytes"] = sizeBytes
        };
    }

    private static string WriteJsonArtifact(
        AppSessionSnapshot snapshot,
        string suffix,
        JsonNode json)
    {
        var artifactDirectory = EnsureArtifactDirectory(snapshot);
        var fileName = $"{suffix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var artifactPath = Path.Combine(artifactDirectory, fileName);
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(json, JsonUtil.Pretty));
        return artifactPath;
    }

    private static string EnsureArtifactDirectory(AppSessionSnapshot snapshot)
    {
        var sanitizedAppId = SanitizePathSegment(snapshot.AppId);
        var sanitizedSessionId = SanitizePathSegment(snapshot.SessionId);
        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "tool-artifacts",
            sanitizedAppId,
            sanitizedSessionId);
        Directory.CreateDirectory(artifactDirectory);
        return artifactDirectory;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}
