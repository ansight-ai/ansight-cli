using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed class ScriptedSessionSanitizationProcessor : ISessionArchiveSanitizer
{
    private static readonly JsonSerializerOptions snapshotJsonOptions = new()
    {
        MaxDepth = JsonUtil.MaximumDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> evidencePropertyNames = new(
        [
            "agentTaskLinks", "analyses", "annotations", "applicationEvents", "artifactSnapshots",
            "images", "logs", "logStreams", "metricChannels", "metrics", "networkRequests", "touches",
            "visualTreeSnapshots"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> textArtifactExtensions = new(
        [
            ".csv", ".htm", ".html", ".json", ".jsonl", ".log", ".md", ".text", ".toml",
            ".tsv", ".txt", ".xml", ".yaml", ".yml"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> protectedIdentityProperties = new(
        [
            "actionid", "agentid", "analysisid", "annotationid", "appid", "capturegroupid",
            "channelid", "configid", "eventid", "frameid", "geometryid", "processsessionid",
            "id", "runid", "screenshotframeid", "sessionid", "snapshotid", "sourcesessionid", "streamid",
            "taskid", "testid", "visualtreesnapshotid"
        ],
        StringComparer.Ordinal);

    private readonly JavaScriptSessionSanitizerExecutor executor;
    private readonly ISessionScreenshotOcrScanner ocrScanner;
    private readonly SessionSanitizationReportBuilder report;
    private readonly Dictionary<string, SessionScreenshotWriteInstruction> screenshotInstructions = new(StringComparer.Ordinal);
    private readonly string sessionCapturesRootPath;
    private readonly string appId;
    private readonly string sessionId;
    private readonly JsonObject operationContext;
    private readonly Action<SessionOptimizationProgress>? reportProgress;
    private int completedItemCount;
    private int totalItemCount;

    public ScriptedSessionSanitizationProcessor(
        string modulePath,
        string javaScriptExecutablePath,
        string sessionCapturesRootPath,
        string appId,
        string sessionId,
        ISessionScreenshotOcrScanner? ocrScanner = null,
        SessionSanitizerOperationContext? operationContext = null,
        Action<SessionOptimizationProgress>? report = null)
    {
        executor = new JavaScriptSessionSanitizerExecutor(modulePath, javaScriptExecutablePath);
        this.sessionCapturesRootPath = sessionCapturesRootPath;
        this.appId = appId;
        this.sessionId = sessionId;
        this.ocrScanner = ocrScanner ?? new TesseractSessionScreenshotOcrScanner();
        reportProgress = report;
        this.operationContext = JsonSerializer.SerializeToNode(
                operationContext ?? SessionSanitizerOperationContext.Export,
                snapshotJsonOptions)?.AsObject()
            ?? throw new InvalidDataException("The sanitizer operation context could not be prepared.");
        this.report = new SessionSanitizationReportBuilder(Path.GetFileNameWithoutExtension(modulePath));
    }

    public SessionSanitizedCapture Sanitize(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var root = JsonSerializer.SerializeToNode(snapshot, snapshotJsonOptions)?.AsObject()
                   ?? throw new InvalidDataException("The session snapshot could not be prepared for sanitization.");
        completedItemCount = 0;
        totalItemCount = CountSanitizerInvocations(root, snapshot.Images.Count);

        SanitizeSessionMetadata(root);
        TransformArray(root, "logs", "sanitizeLog");
        TransformLogStreams(root);
        TransformArray(root, "applicationEvents", "sanitizeApplicationEvent");
        TransformNetworkRequests(root);
        TransformArray(root, "visualTreeSnapshots", "sanitizeVisualTree");
        TransformArray(root, "annotations", "sanitizeAnnotation");
        TransformArray(root, "analyses", "sanitizeAnalysis");
        TransformArray(root, "artifactSnapshots", "sanitizeArtifact");
        TransformArray(root, "agentTaskLinks", "sanitizeDefault");
        TransformArray(root, "metricChannels", "sanitizeDefault");
        TransformArray(root, "metrics", "sanitizeDefault");
        TransformArray(root, "touches", "sanitizeDefault");
        SanitizeScreenshots(snapshot, root);
        UpdateRetainedCounts(root);

        var sanitized = root.Deserialize<AppSessionSnapshot>(snapshotJsonOptions)
                        ?? throw new InvalidDataException("The sanitized session snapshot could not be reconstructed.");
        var regions = screenshotInstructions.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<SessionSanitizationRegion>)pair.Value.Regions,
            StringComparer.Ordinal);
        var policy = new SessionSanitizationPolicy { Id = report.PolicyId };
        return new SessionSanitizedCapture(sanitized, policy, report, regions);
    }

    public bool TryWriteScreenshot(
        string sourceFilePath,
        SessionImageFrame frame,
        ZipArchive archive,
        string entryName,
        IReadOnlyList<SessionSanitizationRegion> sensitiveRegions)
    {
        var instruction = screenshotInstructions.GetValueOrDefault(frame.FrameId)
                          ?? SessionScreenshotWriteInstruction.RedactAll;
        if (instruction.Action == SessionScreenshotWriteAction.Remove)
        {
            report.ScreenshotsRemoved++;
            return false;
        }

        if (instruction.Action == SessionScreenshotWriteAction.Keep)
        {
            archive.CreateEntryFromFile(sourceFilePath, entryName, CompressionLevel.NoCompression);
            return true;
        }

        using var bitmap = SKBitmap.Decode(sourceFilePath)
                           ?? throw new InvalidDataException($"Screenshot '{sourceFilePath}' could not be decoded.");
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
        {
            if (instruction.Action == SessionScreenshotWriteAction.RedactAll || instruction.Regions.Count == 0)
            {
                canvas.Clear(SKColors.Black);
                report.ScreenshotRegionsRedacted++;
            }
            else
            {
                foreach (var region in instruction.Regions)
                {
                    const int padding = 4;
                    var rectangle = new SKRect(
                        Math.Max(0, (float)region.X - padding),
                        Math.Max(0, (float)region.Y - padding),
                        Math.Min(bitmap.Width, (float)(region.X + region.Width) + padding),
                        Math.Min(bitmap.Height, (float)(region.Y + region.Height) + padding));
                    if (rectangle.Width <= 0 || rectangle.Height <= 0)
                    {
                        continue;
                    }

                    canvas.DrawRect(rectangle, paint);
                    report.ScreenshotRegionsRedacted++;
                }
            }

            canvas.Flush();
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(
            ResolveImageFormat(frame.Format, sourceFilePath),
            ResolveImageEncodingQuality(frame));
        using var output = entry.Open();
        encoded.SaveTo(output);
        report.ScreenshotsRedacted++;
        return true;
    }

    public IReadOnlySet<string> WriteArtifacts(
        string artifactsDirectoryPath,
        ZipArchive archive,
        Func<string, string> buildEntryName,
        IReadOnlyDictionary<string, string>? sanitizedRelativePaths = null)
    {
        var writtenPaths = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(artifactsDirectoryPath))
        {
            return writtenPaths;
        }

        var files = Directory.EnumerateFiles(artifactsDirectoryPath, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            var filePath = files[fileIndex];
            reportProgress?.Invoke(new SessionOptimizationProgress(
                $"Sanitizing artifact '{Path.GetFileName(filePath)}'…",
                fileIndex,
                files.Length));
            var fileInfo = new FileInfo(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var isText = textArtifactExtensions.Contains(extension) && fileInfo.Length <= 8 * 1024 * 1024;
            var item = new JsonObject
            {
                ["name"] = Path.GetFileName(filePath),
                ["extension"] = extension,
                ["sizeBytes"] = fileInfo.Length,
                ["isBinary"] = !isText,
                ["content"] = isText ? File.ReadAllText(filePath) : null
            };
            var result = Invoke("sanitizeArtifact", item, itemDescription: $"artifact '{Path.GetFileName(filePath)}'");
            if (result.WasRemoved || result.Value is null)
            {
                report.ArtifactsExcluded++;
                continue;
            }

            var relativePath = Path.GetRelativePath(artifactsDirectoryPath, filePath);
            var outputRelativePath = ResolveArtifactRelativePath(relativePath, sanitizedRelativePaths);
            var safeEntryName = buildEntryName(outputRelativePath);
            if (!isText)
            {
                if (result.Value["keepBinary"]?.GetValue<bool>() == true)
                {
                    archive.CreateEntryFromFile(filePath, safeEntryName, CompressionLevel.NoCompression);
                    writtenPaths.Add(outputRelativePath);
                }
                else
                {
                    report.ArtifactsExcluded++;
                }

                continue;
            }

            if (result.Value["content"] is not JsonValue contentValue
                || !contentValue.TryGetValue<string>(out var content))
            {
                report.ArtifactsExcluded++;
                continue;
            }

            var entry = archive.CreateEntry(safeEntryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(content);
            writtenPaths.Add(outputRelativePath);
            report.ArtifactsSanitized++;
        }

        return writtenPaths;
    }

    private static string NormalizeArtifactRelativePath(string path)
        => path.Replace('\\', '/').Trim('/');

    private static string ResolveArtifactRelativePath(
        string path,
        IReadOnlyDictionary<string, string>? sanitizedRelativePaths)
    {
        var normalized = NormalizeArtifactRelativePath(path);
        return sanitizedRelativePaths?.GetValueOrDefault(normalized) ?? normalized;
    }

    public void Dispose() => executor.Dispose();

    private void SanitizeSessionMetadata(JsonObject root)
    {
        var metadata = new JsonObject();
        foreach (var property in root)
        {
            if (!evidencePropertyNames.Contains(property.Key))
            {
                metadata[property.Key] = property.Value?.DeepClone();
            }
        }

        var result = Invoke("sanitizeSession", metadata, itemDescription: "session metadata");
        if (result.WasRemoved || result.Value is null)
        {
            throw new InvalidDataException("sanitizeSession cannot remove the session.");
        }

        RestoreProtectedProperties(metadata, result.Value);
        foreach (var propertyName in root.Select(static property => property.Key).ToArray())
        {
            if (!evidencePropertyNames.Contains(propertyName))
            {
                root.Remove(propertyName);
            }
        }

        foreach (var property in result.Value)
        {
            if (!evidencePropertyNames.Contains(property.Key))
            {
                root[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private void TransformArray(JsonObject root, string propertyName, string handler)
    {
        if (root[propertyName] is not JsonArray source)
        {
            return;
        }

        var transformed = new JsonArray();
        var items = source.OfType<JsonObject>().ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var result = Invoke(
                handler,
                item,
                itemDescription: $"{propertyName} item {index + 1:N0} of {items.Length:N0}");
            if (result.WasRemoved || result.Value is null)
            {
                continue;
            }

            RestoreProtectedProperties(item, result.Value);
            transformed.Add(result.Value);
        }

        root[propertyName] = transformed;
    }

    private void TransformLogStreams(JsonObject root)
    {
        if (root["logStreams"] is not JsonArray streams)
        {
            return;
        }

        var transformedStreams = new JsonArray();
        foreach (var stream in streams.OfType<JsonObject>())
        {
            var streamId = stream["streamId"]?.GetValue<string>() ?? "unknown";
            var streamMetadata = stream.DeepClone().AsObject();
            streamMetadata.Remove("entries");
            var result = Invoke(
                "sanitizeDefault",
                streamMetadata,
                itemDescription: $"log stream '{streamId}' metadata");
            if (result.WasRemoved || result.Value is null)
            {
                continue;
            }

            RestoreProtectedProperties(stream, result.Value);
            var transformedEntries = new JsonArray();
            if (stream["entries"] is JsonArray entries)
            {
                var sourceEntries = entries.OfType<JsonObject>().ToArray();
                for (var index = 0; index < sourceEntries.Length; index++)
                {
                    var entry = sourceEntries[index];
                    var entryResult = Invoke(
                        "sanitizeLog",
                        entry,
                        itemDescription: $"log stream '{streamId}' entry {index + 1:N0} of {sourceEntries.Length:N0}");
                    if (!entryResult.WasRemoved && entryResult.Value is not null)
                    {
                        RestoreProtectedProperties(entry, entryResult.Value);
                        transformedEntries.Add(entryResult.Value);
                    }
                }
            }

            result.Value["entries"] = transformedEntries;
            result.Value["totalEntryCount"] = transformedEntries.Count;
            result.Value["retainedEntryStartIndex"] = 0;

            transformedStreams.Add(result.Value);
        }

        root["logStreams"] = transformedStreams;
    }

    private void TransformNetworkRequests(JsonObject root)
    {
        if (root["networkRequests"] is not JsonArray source)
        {
            return;
        }

        var transformed = new JsonArray();
        var items = source.OfType<JsonObject>().ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var requestId = item["id"]?.GetValue<string>() ?? $"item {index + 1:N0}";
            var result = Invoke(
                "sanitizeNetworkRequest",
                item,
                itemDescription: $"network request '{requestId}' ({index + 1:N0} of {items.Length:N0})");
            if (result.WasRemoved || result.Value is null)
            {
                continue;
            }

            RestoreProtectedProperties(item, result.Value);
            var request = result.Value.Deserialize<SessionNetworkRequest>(snapshotJsonOptions);
            var normalized = SessionNetworkRequestSanitizer.Normalize(request);
            if (normalized is not null)
            {
                transformed.Add(JsonSerializer.SerializeToNode(normalized, snapshotJsonOptions));
            }
        }

        root["networkRequests"] = transformed;
    }

    private void SanitizeScreenshots(AppSessionSnapshot snapshot, JsonObject root)
    {
        var retained = new JsonArray();
        foreach (var frame in snapshot.Images)
        {
            var frameNode = JsonSerializer.SerializeToNode(frame, snapshotJsonOptions)?.AsObject()
                            ?? throw new InvalidDataException("A screenshot descriptor could not be prepared for sanitization.");
            var sourceImagePath = SessionImageArtifactPath.ResolveCapturedImagePath(
                sessionCapturesRootPath,
                appId,
                sessionId,
                frame);
            var ocr = File.Exists(sourceImagePath)
                ? ocrScanner.Scan(sourceImagePath)
                : SessionScreenshotOcrResult.Unavailable("The screenshot file was unavailable.");
            var context = CreateInvocationContext();
            context["ocr"] = JsonSerializer.SerializeToNode(ocr, snapshotJsonOptions);
            context["visualText"] = JsonSerializer.SerializeToNode(
                ExtractVisualText(snapshot, frame),
                snapshotJsonOptions);
            var result = Invoke(
                "sanitizeScreenshot",
                frameNode,
                context,
                $"screenshot '{frame.FrameId}'");
            var instruction = ParseScreenshotInstruction(result);
            screenshotInstructions[frame.FrameId] = instruction;
            if (instruction.Action != SessionScreenshotWriteAction.Remove)
            {
                retained.Add(frameNode);
            }
        }

        root["images"] = retained;
    }

    private SessionSanitizerInvocationResult Invoke(
        string handler,
        JsonObject item,
        JsonObject? context = null,
        string? itemDescription = null)
    {
        if (itemDescription is not null)
        {
            reportProgress?.Invoke(new SessionOptimizationProgress(
                $"Sanitizing {itemDescription}…",
                completedItemCount,
                totalItemCount));
        }

        SessionSanitizerInvocationResult result;
        try
        {
            result = executor.Invoke(handler, item, context ?? CreateInvocationContext());
        }
        catch (TimeoutException exception) when (itemDescription is not null)
        {
            throw new TimeoutException(
                $"Sanitizer failed while processing {itemDescription}: {exception.Message}",
                exception);
        }
        catch (InvalidDataException exception) when (itemDescription is not null)
        {
            throw new InvalidDataException(
                $"Sanitizer failed while processing {itemDescription}: {exception.Message}",
                exception);
        }

        report.StringsScanned++;
        if (result.RedactionCount > 0)
        {
            report.RecordRedaction("script", result.RedactionCount);
        }

        completedItemCount++;
        if (itemDescription is not null)
        {
            reportProgress?.Invoke(new SessionOptimizationProgress(
                $"Sanitized {itemDescription}.",
                completedItemCount,
                totalItemCount));
        }

        return result;
    }

    private static int CountSanitizerInvocations(JsonObject root, int screenshotCount)
    {
        var total = 1 + screenshotCount;
        foreach (var propertyName in new[]
                 {
                     "logs", "applicationEvents", "networkRequests", "visualTreeSnapshots", "annotations",
                     "analyses", "artifactSnapshots", "agentTaskLinks", "metricChannels", "metrics", "touches"
                 })
        {
            total += root[propertyName] is JsonArray items ? items.OfType<JsonObject>().Count() : 0;
        }

        if (root["logStreams"] is JsonArray streams)
        {
            foreach (var stream in streams.OfType<JsonObject>())
            {
                total++;
                total += stream["entries"] is JsonArray entries ? entries.OfType<JsonObject>().Count() : 0;
            }
        }

        return Math.Max(1, total);
    }

    private JsonObject CreateInvocationContext()
        => new()
        {
            ["operation"] = operationContext.DeepClone()
        };

    private static SessionScreenshotWriteInstruction ParseScreenshotInstruction(
        SessionSanitizerInvocationResult result)
    {
        if (result.WasRemoved || result.Value is null)
        {
            return SessionScreenshotWriteInstruction.Remove;
        }

        if (result.Value["sanitization"] is not JsonObject sanitization
            || sanitization["action"] is not JsonValue actionValue
            || !actionValue.TryGetValue<string>(out var action))
        {
            return SessionScreenshotWriteInstruction.RedactAll;
        }

        return action switch
        {
            "keep" => SessionScreenshotWriteInstruction.Keep,
            "remove" => SessionScreenshotWriteInstruction.Remove,
            "redactAll" => SessionScreenshotWriteInstruction.RedactAll,
            "redact" => new SessionScreenshotWriteInstruction(
                SessionScreenshotWriteAction.Redact,
                ParseRegions(sanitization["regions"] as JsonArray)),
            _ => SessionScreenshotWriteInstruction.RedactAll
        };
    }

    private static IReadOnlyList<SessionSanitizationRegion> ParseRegions(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var regions = new List<SessionSanitizationRegion>();
        foreach (var item in array.OfType<JsonObject>())
        {
            if (TryReadDouble(item, "x", out var x)
                && TryReadDouble(item, "y", out var y)
                && TryReadDouble(item, "width", out var width)
                && TryReadDouble(item, "height", out var height)
                && width > 0
                && height > 0)
            {
                regions.Add(new SessionSanitizationRegion(x, y, width, height));
            }
        }

        return regions;
    }

    private static IReadOnlyList<SessionScreenshotTextBlock> ExtractVisualText(
        AppSessionSnapshot snapshot,
        SessionImageFrame frame)
    {
        var tree = snapshot.VisualTreeSnapshots
            .Where(candidate => string.Equals(candidate.ScreenshotFrameId, frame.FrameId, StringComparison.Ordinal))
            .OrderByDescending(static candidate => candidate.CapturedAtUtc)
            .FirstOrDefault();
        if (tree is null)
        {
            return [];
        }

        var result = new List<SessionScreenshotTextBlock>();
        var coordinateSpace = ReadBounds(tree.Payload["coordinateSpace"] as JsonObject)
                              ?? ReadBounds(tree.Payload["root"] as JsonObject)
                              ?? new SessionSanitizationRegion(0, 0, frame.Width, frame.Height);
        VisitObjects(tree.Payload, candidate =>
        {
            var text = ReadFirstString(candidate, "text", "label", "value", "accessibilityLabel", "contentDescription");
            if (string.IsNullOrWhiteSpace(text) || ReadBounds(candidate) is not { } bounds)
            {
                return;
            }

            var scaleX = coordinateSpace.Width <= 0 ? 1 : frame.Width / coordinateSpace.Width;
            var scaleY = coordinateSpace.Height <= 0 ? 1 : frame.Height / coordinateSpace.Height;
            result.Add(new SessionScreenshotTextBlock(
                text,
                100,
                new SessionSanitizationRegion(
                    (bounds.X - coordinateSpace.X) * scaleX,
                    (bounds.Y - coordinateSpace.Y) * scaleY,
                    bounds.Width * scaleX,
                    bounds.Height * scaleY)));
        });
        return result;
    }

    private static void VisitObjects(JsonNode node, Action<JsonObject> visitor)
    {
        if (node is JsonObject jsonObject)
        {
            visitor(jsonObject);
            foreach (var child in jsonObject.Select(static property => property.Value).OfType<JsonNode>())
            {
                VisitObjects(child, visitor);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.OfType<JsonNode>())
            {
                VisitObjects(child, visitor);
            }
        }
    }

    private static string? ReadFirstString(JsonObject candidate, params string[] names)
    {
        foreach (var name in names)
        {
            if (candidate[name] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static SessionSanitizationRegion? ReadBounds(JsonObject? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node["bounds"] is JsonArray array)
        {
            var offset = array.Count >= 8 ? 4 : 0;
            return TryReadDouble(array, offset, out var arrayX)
                   && TryReadDouble(array, offset + 1, out var arrayY)
                   && TryReadDouble(array, offset + 2, out var arrayWidth)
                   && TryReadDouble(array, offset + 3, out var arrayHeight)
                ? new SessionSanitizationRegion(arrayX, arrayY, arrayWidth, arrayHeight)
                : null;
        }

        var bounds = node["bounds"] as JsonObject ?? node;
        var absolute = bounds["absoluteX"] is not null;
        return TryReadDouble(bounds, absolute ? "absoluteX" : "x", out var x)
               && TryReadDouble(bounds, absolute ? "absoluteY" : "y", out var y)
               && TryReadDouble(bounds, absolute ? "absoluteWidth" : "width", out var width)
               && TryReadDouble(bounds, absolute ? "absoluteHeight" : "height", out var height)
            ? new SessionSanitizationRegion(x, y, width, height)
            : null;
    }

    private static bool TryReadDouble(JsonObject node, string propertyName, out double result)
    {
        result = 0;
        return node[propertyName] is JsonValue value && TryReadDouble(value, out result);
    }

    private static bool TryReadDouble(JsonArray array, int index, out double result)
    {
        result = 0;
        return index >= 0 && index < array.Count && array[index] is JsonValue value && TryReadDouble(value, out result);
    }

    private static bool TryReadDouble(JsonValue value, out double result)
    {
        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        return false;
    }

    private static void RestoreProtectedProperties(JsonObject original, JsonObject sanitized)
    {
        foreach (var property in original)
        {
            if (IsProtectedProperty(property.Key))
            {
                sanitized[property.Key] = property.Value?.DeepClone();
                continue;
            }

            if (property.Value is JsonObject originalObject
                && sanitized[property.Key] is JsonObject sanitizedObject)
            {
                RestoreProtectedProperties(originalObject, sanitizedObject);
            }
            else if (property.Value is JsonArray originalArray
                     && sanitized[property.Key] is JsonArray sanitizedArray)
            {
                if (originalArray.Count != sanitizedArray.Count)
                {
                    continue;
                }

                for (var index = 0; index < originalArray.Count; index++)
                {
                    if (originalArray[index] is JsonObject originalItem
                        && sanitizedArray[index] is JsonObject sanitizedItem)
                    {
                        RestoreProtectedProperties(originalItem, sanitizedItem);
                    }
                }
            }
        }
    }

    private static bool IsProtectedProperty(string propertyName)
    {
        var normalized = new string(propertyName.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return protectedIdentityProperties.Contains(normalized)
               || normalized.EndsWith("utc", StringComparison.Ordinal)
               || normalized is "timestamp" or "timestampms";
    }

    private static void UpdateRetainedCounts(JsonObject root)
    {
        root["totalLogCount"] = (root["logs"] as JsonArray)?.Count ?? 0;
        root["retainedLogStartIndex"] = 0;
        root["totalAnnotationCount"] = (root["annotations"] as JsonArray)?.Count ?? 0;
        root["totalImageCount"] = (root["images"] as JsonArray)?.Count ?? 0;
        root["totalMetricChannelCount"] = (root["metricChannels"] as JsonArray)?.Count ?? 0;
        root["totalMetricSampleCount"] = (root["metrics"] as JsonArray)?.Count ?? 0;
        root["totalApplicationEventCount"] = (root["applicationEvents"] as JsonArray)?.Count ?? 0;
        root["totalNetworkRequestCount"] = (root["networkRequests"] as JsonArray)?.Count ?? 0;
    }

    private static SKEncodedImageFormat ResolveImageFormat(string? format, string filePath)
    {
        var normalized = (format ?? Path.GetExtension(filePath)).Trim().TrimStart('.').ToLowerInvariant();
        return normalized switch
        {
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };
    }

    private static int ResolveImageEncodingQuality(SessionImageFrame frame)
        => string.Equals(frame.Format?.Trim().TrimStart('.'), "webp", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(frame.Quality, 0, 100)
            : 92;
}

internal enum SessionScreenshotWriteAction
{
    Keep,
    Redact,
    RedactAll,
    Remove
}

internal sealed record SessionScreenshotWriteInstruction(
    SessionScreenshotWriteAction Action,
    IReadOnlyList<SessionSanitizationRegion> Regions)
{
    public static SessionScreenshotWriteInstruction Keep { get; } = new(SessionScreenshotWriteAction.Keep, []);

    public static SessionScreenshotWriteInstruction RedactAll { get; } = new(SessionScreenshotWriteAction.RedactAll, []);

    public static SessionScreenshotWriteInstruction Remove { get; } = new(SessionScreenshotWriteAction.Remove, []);
}
