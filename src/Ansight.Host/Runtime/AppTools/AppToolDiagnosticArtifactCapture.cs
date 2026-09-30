namespace Ansight.Host.Runtime.AppTools;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;
using Ansight.Tools;

internal static class AppToolDiagnosticArtifactCapture
{
    private const string ArtifactSource = "ansight.app-tool.diagnostic";
    private const string FileDescriptorListToolId = "file_descriptors.list_open";
    private const string JniReferenceGraphToolId = "jni_references.capture_graph";
    private static readonly IReadOnlyDictionary<string, DiagnosticArtifactDefinition> definitions =
        new Dictionary<string, DiagnosticArtifactDefinition>(StringComparer.Ordinal)
        {
            [FileDescriptorListToolId] = new(
                RootAlias: "file-descriptors",
                FileName: "open-file-descriptors.json",
                Name: "Open file descriptors",
                Kind: "file-descriptor-snapshot"),
            [JniReferenceGraphToolId] = new(
                RootAlias: "jni-references",
                FileName: "jni-reference-graph.json",
                Name: "JNI object-reference graph",
                Kind: "jni-reference-graph")
        };

    public static AppToolDiagnosticArtifactCaptureResult CaptureIfSupported(
        ISessionIngestion sessionIngestion,
        string sessionId,
        string? toolId,
        ToolProtocolEnvelope response)
    {
        ArgumentNullException.ThrowIfNull(sessionIngestion);
        ArgumentNullException.ThrowIfNull(response);

        if (string.IsNullOrWhiteSpace(toolId)
            || !definitions.TryGetValue(toolId.Trim(), out var definition))
        {
            return AppToolDiagnosticArtifactCaptureResult.NotSupported;
        }

        if (string.Equals(response.Type, ToolProtocolMessageTypes.ErrorType, StringComparison.Ordinal)
            || response.Payload is not JsonObject payload
            || payload["result"] is not JsonObject
            || IsExplicitFailure(payload))
        {
            return new AppToolDiagnosticArtifactCaptureResult(
                IsSupported: true,
                IsCaptured: false,
                SnapshotId: null,
                Message: null);
        }

        var capturedAtUtc = ResolveCapturedAtUtc(payload);
        var snapshotId = $"app-tool-diagnostic-{Guid.NewGuid():N}";
        var artifactDirectoryName = $"{capturedAtUtc:yyyyMMdd-HHmmssfff}-{snapshotId}";
        var sourceDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "AnsightHost",
            "session-diagnostic-captures",
            FileNameUtil.Sanitize(sessionId),
            artifactDirectoryName);
        var sourceFilePath = Path.Combine(sourceDirectoryPath, definition.FileName);

        try
        {
            Directory.CreateDirectory(sourceDirectoryPath);
            var document = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["capturedAtUtc"] = capturedAtUtc,
                ["sessionId"] = sessionId,
                ["toolId"] = toolId.Trim(),
                ["responseType"] = response.Type,
                ["payload"] = payload.DeepClone()
            };
            File.WriteAllText(sourceFilePath, JsonSerializer.Serialize(document, JsonUtil.Pretty));

            var sourceFileInfo = new FileInfo(sourceFilePath);
            var entry = new SessionArtifactEntry
            {
                Name = definition.FileName,
                RootAlias = definition.RootAlias,
                RelativePath = definition.FileName,
                SnapshotRelativePath = definition.FileName,
                Kind = "file",
                SizeBytes = sourceFileInfo.Length,
                FileExtension = ".json",
                MimeType = "application/json",
                LastModifiedUtc = sourceFileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                ArchiveRelativePath = definition.FileName
            };
            var snapshot = new SessionArtifactSnapshot
            {
                SnapshotId = snapshotId,
                CapturedAtUtc = capturedAtUtc,
                Source = ArtifactSource,
                RootAlias = definition.RootAlias,
                RootPath = definition.RootAlias,
                RelativePath = definition.FileName,
                Name = definition.Name,
                Kind = definition.Kind,
                ArtifactDirectoryName = artifactDirectoryName,
                DirectoryCount = 0,
                FileCount = 1,
                ByteCount = sourceFileInfo.Length,
                Truncated = false,
                Entries = [entry]
            };

            var result = sessionIngestion.AddSessionArtifactSnapshot(sessionId, snapshot, sourceDirectoryPath);
            return result.IsSuccess
                ? new AppToolDiagnosticArtifactCaptureResult(
                    IsSupported: true,
                    IsCaptured: true,
                    SnapshotId: snapshotId,
                    Message: result.Message)
                : new AppToolDiagnosticArtifactCaptureResult(
                    IsSupported: true,
                    IsCaptured: false,
                    SnapshotId: null,
                    Message: result.Message);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException)
        {
            return new AppToolDiagnosticArtifactCaptureResult(
                IsSupported: true,
                IsCaptured: false,
                SnapshotId: null,
                Message: exception.Message);
        }
        finally
        {
            TryDeleteDirectory(sourceDirectoryPath);
        }
    }

    private static DateTimeOffset ResolveCapturedAtUtc(JsonObject payload)
    {
        var value = payload["result"]?["capturedAtUtc"] is JsonValue capturedAtValue
                    && capturedAtValue.TryGetValue<string>(out var capturedAtText)
            ? capturedAtText
            : null;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var capturedAtUtc)
            ? capturedAtUtc
            : DateTimeOffset.UtcNow;
    }

    private static bool IsExplicitFailure(JsonObject payload)
    {
        return payload["success"] is JsonValue successValue
               && successValue.TryGetValue<bool>(out var success)
               && !success;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup after the capture store has copied the artifact.
        }
    }

    private sealed record DiagnosticArtifactDefinition(
        string RootAlias,
        string FileName,
        string Name,
        string Kind);
}

internal sealed record AppToolDiagnosticArtifactCaptureResult(
    bool IsSupported,
    bool IsCaptured,
    string? SnapshotId,
    string? Message)
{
    public static AppToolDiagnosticArtifactCaptureResult NotSupported { get; } = new(
        IsSupported: false,
        IsCaptured: false,
        SnapshotId: null,
        Message: null);
}
