using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Feedback;

internal static class AnnotatedFeedbackBundleReader
{
    public const string BundleSchema = "ansight.annotation.bundle.v1";
    public const string BundleMimeType = "application/vnd.ansight.annotation+zip";
    public const long MaximumBundleBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 256L * 1024 * 1024;

    private static readonly JsonDocumentOptions documentOptions = new()
    {
        AllowTrailingCommas = true
    };

    public static bool TryRead(
        Stream stream,
        string sourceName,
        out AnnotatedFeedbackBundleContent? content,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(stream);
        content = null;
        error = null;

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var manifestEntry = FindEntry(archive, "manifest.json");
            if (manifestEntry is null)
            {
                error = $"Annotated feedback bundle '{sourceName}' is missing manifest.json.";
                return false;
            }

            using var manifestStream = manifestEntry.Open();
            using var document = JsonDocument.Parse(manifestStream, documentOptions);
            var manifest = document.RootElement;
            if (!string.Equals(ReadString(manifest, "schema"), BundleSchema, StringComparison.Ordinal))
            {
                error = $"Annotated feedback bundle '{sourceName}' uses an unsupported schema.";
                return false;
            }

            var annotationId = NormalizeGuidIdentifier(ReadString(manifest, "annotationId"));
            if (annotationId is null)
            {
                error = $"Annotated feedback bundle '{sourceName}' is missing a valid annotation id.";
                return false;
            }

            var capturedAtUtc = ReadTimestamp(manifest, "capturedAtUtc") ?? DateTimeOffset.UtcNow;
            var screenshot = ReadScreenshot(archive, manifest);
            var visualTrees = ReadVisualTrees(archive, manifest, annotationId, capturedAtUtc);
            var artifacts = ReadArtifacts(archive, manifest);
            var evidence = ReadEvidence(manifest).ToList();
            foreach (var artifact in artifacts)
            {
                if (evidence.Any(item => string.Equals(item.Id, artifact.EvidenceId, StringComparison.Ordinal)))
                {
                    continue;
                }

                evidence.Add(new SessionAnnotationEvidence
                {
                    Id = artifact.EvidenceId,
                    Kind = "artifact",
                    Status = artifact.Status,
                    Reason = artifact.Reason,
                    CapturedAtUtc = string.Equals(artifact.Status, "captured", StringComparison.OrdinalIgnoreCase)
                        ? capturedAtUtc
                        : null,
                    SizeBytes = artifact.SizeBytes
                });
            }

            content = new AnnotatedFeedbackBundleContent
            {
                AnnotationId = annotationId,
                CaptureGroupId = NormalizeGuidIdentifier(ReadString(manifest, "captureGroupId")),
                CapturedAtUtc = capturedAtUtc,
                Feedback = NormalizeText(ReadString(manifest, "feedback")),
                Shapes = ReadShapes(manifest),
                Screenshot = screenshot,
                VisualTrees = visualTrees,
                Artifacts = artifacts,
                CustomData = ReadObject(manifest, "customData"),
                Evidence = evidence,
                HookFailures = ReadStringArray(manifest, "hookFailures")
            };
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            error = $"Unable to read annotated feedback bundle '{sourceName}': {exception.Message}";
            return false;
        }
    }

    private static AnnotatedFeedbackScreenshot? ReadScreenshot(ZipArchive archive, JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "screenshot", out var screenshotElement)
            || screenshotElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var path = ReadString(screenshotElement, "path");
        var entry = FindEntry(archive, path);
        if (entry is null)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = ReadEntryBytes(entry, MaximumEntryBytes);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        return bytes.Length == 0
            ? null
            : new AnnotatedFeedbackScreenshot(
                NormalizeImageFormat(ReadString(screenshotElement, "mimeType"), path),
                Math.Max(0, ReadInt(screenshotElement, "width") ?? 0),
                Math.Max(0, ReadInt(screenshotElement, "height") ?? 0),
                ReadTimestamp(screenshotElement, "capturedAtUtc") ?? DateTimeOffset.UtcNow,
                bytes);
    }

    private static IReadOnlyList<AnnotatedFeedbackVisualTree> ReadVisualTrees(
        ZipArchive archive,
        JsonElement manifest,
        string annotationId,
        DateTimeOffset capturedAtUtc)
    {
        if (!TryGetProperty(manifest, "visualTrees", out var visualTreesElement)
            || visualTreesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AnnotatedFeedbackVisualTree>();
        }

        var visualTrees = new List<AnnotatedFeedbackVisualTree>();
        var index = 0;
        foreach (var element in visualTreesElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = FindEntry(archive, ReadString(element, "path"));
            if (entry is null)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = ReadEntryBytes(entry, MaximumEntryBytes);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            JsonObject? payload;
            try
            {
                payload = JsonNode.Parse(bytes, documentOptions: documentOptions) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (payload is null || payload.Count == 0)
            {
                continue;
            }

            var source = NormalizeText(ReadString(element, "source")) ?? $"visual-tree-{index + 1}";
            visualTrees.Add(new AnnotatedFeedbackVisualTree(
                $"{annotationId}-visual-tree-{index:D3}-{SanitizeIdentifier(source)}",
                source,
                NormalizeText(ReadString(element, "displayName")) ?? source,
                ReadTimestamp(element, "capturedAtUtc") ?? capturedAtUtc,
                ReadBool(element, "truncated") ?? false,
                payload));
            index++;
        }

        return visualTrees;
    }

    private static IReadOnlyList<AnnotatedFeedbackArtifact> ReadArtifacts(ZipArchive archive, JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "artifacts", out var artifactsElement)
            || artifactsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AnnotatedFeedbackArtifact>();
        }

        var artifacts = new List<AnnotatedFeedbackArtifact>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in artifactsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = NormalizeText(ReadString(element, "name")) ?? $"Artifact {index + 1}";
            var status = NormalizeText(ReadString(element, "status"))?.ToLowerInvariant() ?? "unknown";
            byte[]? bytes = null;
            if (string.Equals(status, "captured", StringComparison.Ordinal))
            {
                var entry = FindEntry(archive, ReadString(element, "path"));
                if (entry is not null)
                {
                    try
                    {
                        bytes = ReadEntryBytes(entry, MaximumEntryBytes);
                    }
                    catch (InvalidDataException exception)
                    {
                        status = "skipped";
                        bytes = null;
                        artifacts.Add(new AnnotatedFeedbackArtifact(
                            $"artifact:{index}:{name}",
                            name,
                            NormalizeText(ReadString(element, "kind")) ?? "artifact",
                            NormalizeText(ReadString(element, "mimeType")) ?? "application/octet-stream",
                            CreateUniqueFileName(
                                NormalizeText(ReadString(element, "fileName")) ?? $"artifact-{index + 1:D3}.bin",
                                index,
                                usedFileNames),
                            status,
                            exception.Message,
                            ReadLong(element, "sizeBytes"),
                            null));
                        index++;
                        continue;
                    }
                }

                if (bytes is null || bytes.Length == 0)
                {
                    status = "unavailable";
                }
            }

            var fileName = CreateUniqueFileName(
                NormalizeText(ReadString(element, "fileName")) ?? $"artifact-{index + 1:D3}.bin",
                index,
                usedFileNames);
            artifacts.Add(new AnnotatedFeedbackArtifact(
                $"artifact:{index}:{name}",
                name,
                NormalizeText(ReadString(element, "kind")) ?? "artifact",
                NormalizeText(ReadString(element, "mimeType")) ?? "application/octet-stream",
                fileName,
                status,
                NormalizeText(ReadString(element, "reason")),
                ReadLong(element, "sizeBytes") ?? bytes?.LongLength,
                bytes));
            index++;
        }

        return artifacts;
    }

    private static IReadOnlyList<AnnotatedFeedbackShape> ReadShapes(JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "shapes", out var shapesElement)
            || shapesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AnnotatedFeedbackShape>();
        }

        var shapes = new List<AnnotatedFeedbackShape>();
        foreach (var element in shapesElement.EnumerateArray())
        {
            var kind = ReadString(element, "kind")?.Trim().ToLowerInvariant();
            if (kind == "freedraw")
            {
                var points = ReadShapePoints(element);
                if (points.Count < 2)
                {
                    continue;
                }

                var pathX = points.Min(point => point.X);
                var pathY = points.Min(point => point.Y);
                var pathWidth = points.Max(point => point.X) - pathX;
                var pathHeight = points.Max(point => point.Y) - pathY;
                if (pathWidth <= 0d && pathHeight <= 0d)
                {
                    continue;
                }

                shapes.Add(new AnnotatedFeedbackShape(
                    "freeDraw",
                    pathX,
                    pathY,
                    pathWidth,
                    pathHeight,
                    points,
                    NormalizeText(ReadString(element, "text")),
                    NormalizeText(ReadString(element, "strokeColor")),
                    ReadDouble(element, "strokeWidth")));
                continue;
            }

            if (kind is not ("rectangle" or "ellipse"))
            {
                continue;
            }

            var x = Math.Clamp(ReadDouble(element, "x") ?? 0d, 0d, 1d);
            var y = Math.Clamp(ReadDouble(element, "y") ?? 0d, 0d, 1d);
            var width = Math.Clamp(ReadDouble(element, "width") ?? 0d, 0d, 1d - x);
            var height = Math.Clamp(ReadDouble(element, "height") ?? 0d, 0d, 1d - y);
            if (width <= 0d || height <= 0d)
            {
                continue;
            }

            shapes.Add(new AnnotatedFeedbackShape(
                kind,
                x,
                y,
                width,
                height,
                Array.Empty<AnnotatedFeedbackPoint>(),
                NormalizeText(ReadString(element, "text")),
                NormalizeText(ReadString(element, "strokeColor")),
                ReadDouble(element, "strokeWidth")));
        }

        return shapes;
    }

    private static IReadOnlyList<AnnotatedFeedbackPoint> ReadShapePoints(JsonElement shape)
    {
        if (!TryGetProperty(shape, "points", out var pointsElement) || pointsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AnnotatedFeedbackPoint>();
        }

        return pointsElement.EnumerateArray()
            .Where(point => point.ValueKind == JsonValueKind.Object)
            .Select(point => new AnnotatedFeedbackPoint(
                Math.Clamp(ReadDouble(point, "x") ?? 0d, 0d, 1d),
                Math.Clamp(ReadDouble(point, "y") ?? 0d, 0d, 1d)))
            .ToArray();
    }

    private static IReadOnlyList<SessionAnnotationEvidence> ReadEvidence(JsonElement manifest)
    {
        if (!TryGetProperty(manifest, "evidence", out var evidenceElement)
            || evidenceElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SessionAnnotationEvidence>();
        }

        return evidenceElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select((element, index) => new SessionAnnotationEvidence
            {
                Id = NormalizeText(ReadString(element, "id")) ?? $"evidence-{index + 1}",
                Kind = NormalizeText(ReadString(element, "kind"))?.ToLowerInvariant() ?? "unknown",
                Status = NormalizeText(ReadString(element, "status"))?.ToLowerInvariant() ?? "unknown",
                Reason = NormalizeText(ReadString(element, "reason")),
                CapturedAtUtc = ReadTimestamp(element, "capturedAtUtc"),
                SizeBytes = ReadLong(element, "sizeBytes"),
                Truncated = ReadBool(element, "truncated") ?? false
            })
            .ToArray();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string? path)
    {
        var normalizedPath = NormalizeArchivePath(path);
        if (normalizedPath is null)
        {
            return null;
        }

        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeArchivePath(entry.FullName), normalizedPath, StringComparison.Ordinal));
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes)
        {
            throw new InvalidDataException($"Bundle entry '{entry.FullName}' exceeds the supported size limit.");
        }

        using var source = entry.Open();
        using var destination = new MemoryStream(entry.Length > 0 ? checked((int)Math.Min(entry.Length, int.MaxValue)) : 0);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"Bundle entry '{entry.FullName}' exceeds the supported size limit.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static JsonObject? ReadObject(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText()) as JsonObject
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => item.GetString()!.Trim())
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static long? ReadLong(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;

    private static double? ReadDouble(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.TryGetDouble(out var parsed) && double.IsFinite(parsed) ? parsed : null;

    private static bool? ReadBool(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeGuidIdentifier(string? value)
        => Guid.TryParse(value, out var parsed) ? parsed.ToString("N") : null;

    private static string? NormalizeArchivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..")
            ? null
            : normalized;
    }

    private static string NormalizeImageFormat(string? mimeType, string? path)
    {
        var candidate = $"{mimeType} {Path.GetExtension(path)}";
        return candidate.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
    }

    private static string CreateUniqueFileName(string fileName, int index, HashSet<string> usedFileNames)
    {
        var sanitized = string.Concat(Path.GetFileName(fileName).Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = $"artifact-{index + 1:D3}.bin";
        }

        var candidate = $"{index:D3}-{sanitized}";
        while (!usedFileNames.Add(candidate))
        {
            candidate = $"{index:D3}-{Guid.NewGuid():N}-{sanitized}";
        }

        return candidate;
    }

    private static string SanitizeIdentifier(string value)
    {
        var result = new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        return string.IsNullOrWhiteSpace(result) ? "item" : result;
    }
}
