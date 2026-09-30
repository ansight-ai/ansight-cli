using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Ansight.Host.Runtime.Sanitization;

internal sealed class SessionSanitizationProcessor : ISessionArchiveSanitizer
{
    private static readonly JsonSerializerOptions snapshotJsonOptions = new()
    {
        MaxDepth = JsonUtil.MaximumDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> protectedProperties = new(
        [
            "actionid", "agentid", "analysisid", "annotationid", "appid", "capturegroupid",
            "channelid", "configid", "eventid", "frameid", "geometryid", "processsessionid",
            "id", "runid", "screenshotframeid", "sessionid", "snapshotid", "sourcesessionid", "streamid",
            "taskid", "testid", "visualtreesnapshotid"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> sensitiveContentProperties = new(
        [
            "accessibilitylabel",
            "contentdescription",
            "label",
            "placeholder",
            "text",
            "title",
            "value"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> sensitiveContextMarkers = new(
        [
            "address",
            "auth",
            "birthdate",
            "cookie",
            "creditcard",
            "email",
            "firstname",
            "fullname",
            "lastname",
            "password",
            "phone",
            "secure",
            "secret",
            "token",
            "username"
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> textArtifactExtensions = new(
        [
            ".csv", ".htm", ".html", ".json", ".jsonl", ".log", ".md", ".text", ".toml",
            ".tsv", ".txt", ".xml", ".yaml", ".yml"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SessionSanitizationPolicy policy;
    private readonly SessionSanitizationReportBuilder report;
    private readonly IReadOnlyList<SessionSanitizationCompiledRule> rules;
    private readonly HashSet<string> sensitiveProperties;
    private readonly Action<SessionOptimizationProgress>? reportProgress;
    private readonly ISessionScreenshotOcrScanner ocrScanner;
    private readonly HashSet<string> framesWithVisualTrees = new(StringComparer.Ordinal);

    public SessionSanitizationProcessor(
        SessionSanitizationPolicy policy,
        Action<SessionOptimizationProgress>? report = null,
        ISessionScreenshotOcrScanner? ocrScanner = null)
    {
        SessionSanitizationPolicyValidator.Validate(policy);
        this.policy = policy;
        reportProgress = report;
        this.ocrScanner = ocrScanner ?? new TesseractSessionScreenshotOcrScanner();
        this.report = new SessionSanitizationReportBuilder(policy.Id.Trim());
        sensitiveProperties = new HashSet<string>(
            policy.SensitiveProperties.Select(NormalizePropertyName),
            StringComparer.Ordinal);
        rules = BuildRules(policy);
    }

    public SessionSanitizedCapture Sanitize(AppSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        framesWithVisualTrees.Clear();
        reportProgress?.Invoke(new SessionOptimizationProgress("Scanning session content for sensitive data…", 0, 1));
        var regionsByFrameId = FindSensitiveScreenshotRegions(snapshot);
        var node = JsonSerializer.SerializeToNode(snapshot, snapshotJsonOptions)
                   ?? throw new InvalidDataException("The session snapshot could not be prepared for sanitization.");
        SanitizeNode(node, null, sensitiveContentContext: false);
        var sanitized = node.Deserialize<AppSessionSnapshot>(snapshotJsonOptions)
                        ?? throw new InvalidDataException("The sanitized session snapshot could not be reconstructed.");

        if (policy.Screenshots.Mode == SessionScreenshotSanitizationMode.Remove)
        {
            report.ScreenshotsRemoved += sanitized.Images.Count;
            sanitized = CloneWithImages(sanitized, []);
        }
        else if (policy.Screenshots.Mode == SessionScreenshotSanitizationMode.SensitiveRegions
                 && policy.Screenshots.Fallback == SessionScreenshotSanitizationFallback.Remove)
        {
            report.ScreenshotsRemoved += sanitized.Images.Count(
                frame => !regionsByFrameId.ContainsKey(frame.FrameId));
            sanitized = CloneWithImages(
                sanitized,
                sanitized.Images.Where(frame => regionsByFrameId.ContainsKey(frame.FrameId)).ToArray());
        }

        if (policy.Artifacts.Mode == SessionArtifactSanitizationMode.Exclude)
        {
            sanitized = CloneWithArtifactSnapshots(sanitized, []);
        }

        reportProgress?.Invoke(new SessionOptimizationProgress("Scanned session content for sensitive data.", 1, 1));
        return new SessionSanitizedCapture(sanitized, policy, report, regionsByFrameId);
    }

    public bool TryWriteScreenshot(
        string sourceFilePath,
        SessionImageFrame frame,
        ZipArchive archive,
        string entryName,
        IReadOnlyList<SessionSanitizationRegion> sensitiveRegions)
    {
        var mode = policy.Screenshots.Mode;
        if (mode == SessionScreenshotSanitizationMode.Remove)
        {
            report.ScreenshotsRemoved++;
            return false;
        }

        if (mode == SessionScreenshotSanitizationMode.Keep)
        {
            archive.CreateEntryFromFile(sourceFilePath, entryName, CompressionLevel.NoCompression);
            return true;
        }

        var redactAll = mode == SessionScreenshotSanitizationMode.RedactAll;
        var regionsToRedact = sensitiveRegions.ToList();
        var inspected = framesWithVisualTrees.Contains(frame.FrameId);
        if (mode == SessionScreenshotSanitizationMode.SensitiveRegions)
        {
            SessionScreenshotOcrResult scan;
            try { scan = ocrScanner.Scan(sourceFilePath); }
            catch (Exception exception) { scan = SessionScreenshotOcrResult.Unavailable(exception.Message); }
            inspected |= scan.Available;
            if (scan.Available)
            {
                regionsToRedact.AddRange(scan.Blocks
                    .Where(block => ContainsSensitiveText(block.Text))
                    .Select(block => block.Bounds));
            }
        }

        if (mode == SessionScreenshotSanitizationMode.SensitiveRegions && regionsToRedact.Count == 0 && !inspected)
        {
            switch (policy.Screenshots.Fallback)
            {
                case SessionScreenshotSanitizationFallback.Keep:
                    archive.CreateEntryFromFile(sourceFilePath, entryName, CompressionLevel.NoCompression);
                    return true;
                case SessionScreenshotSanitizationFallback.Remove:
                    report.ScreenshotsRemoved++;
                    return false;
                case SessionScreenshotSanitizationFallback.RedactAll:
                    redactAll = true;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported screenshot fallback '{policy.Screenshots.Fallback}'.");
            }
        }

        if (!redactAll && regionsToRedact.Count == 0)
        {
            archive.CreateEntryFromFile(sourceFilePath, entryName, CompressionLevel.NoCompression);
            return true;
        }

        using var bitmap = SKBitmap.Decode(sourceFilePath)
                           ?? throw new InvalidDataException($"Screenshot '{sourceFilePath}' could not be decoded.");
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
        {
            if (redactAll)
            {
                canvas.Clear(SKColors.Black);
                report.ScreenshotRegionsRedacted++;
            }
            else
            {
                foreach (var region in regionsToRedact)
                {
                    var padding = policy.Screenshots.PaddingPixels;
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
        if (policy.Artifacts.Mode == SessionArtifactSanitizationMode.Exclude)
        {
            report.ArtifactsExcluded += files.Length;
            return writtenPaths;
        }

        if (policy.Artifacts.Mode == SessionArtifactSanitizationMode.Keep)
        {
            foreach (var filePath in files)
            {
                var relativePath = Path.GetRelativePath(artifactsDirectoryPath, filePath);
                var outputRelativePath = ResolveArtifactRelativePath(relativePath, sanitizedRelativePaths);
                archive.CreateEntryFromFile(filePath, buildEntryName(outputRelativePath), CompressionLevel.NoCompression);
                writtenPaths.Add(outputRelativePath);
            }

            return writtenPaths;
        }

        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            var filePath = files[fileIndex];
            reportProgress?.Invoke(new SessionOptimizationProgress(
                $"Sanitizing artifact '{Path.GetFileName(filePath)}'…",
                fileIndex,
                files.Length));
            var fileInfo = new FileInfo(filePath);
            var extension = Path.GetExtension(filePath);
            if (!textArtifactExtensions.Contains(extension)
                || fileInfo.Length > policy.Artifacts.MaximumTextBytes)
            {
                report.ArtifactsExcluded++;
                continue;
            }

            var source = File.ReadAllText(filePath);
            var sanitized = SanitizeArtifactText(source, extension);
            var relativePath = Path.GetRelativePath(artifactsDirectoryPath, filePath);
            var outputRelativePath = ResolveArtifactRelativePath(relativePath, sanitizedRelativePaths);
            var entry = archive.CreateEntry(buildEntryName(outputRelativePath), CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(sanitized);
            writtenPaths.Add(outputRelativePath);
            report.ArtifactsSanitized++;
            reportProgress?.Invoke(new SessionOptimizationProgress(
                "Sanitizing captured artifacts…",
                fileIndex + 1,
                files.Length));
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

    public void Dispose()
    {
    }

    private string SanitizeArtifactText(string source, string extension)
    {
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var node = JsonNode.Parse(source);
                    if (node is not null)
                    {
                        SanitizeNode(node, null, sensitiveContentContext: false);
                        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    }
                }
                else
                {
                    return string.Join(
                        Environment.NewLine,
                        source.Split('\n').Select(line => SanitizeJsonLine(line.TrimEnd('\r'))));
                }
            }
            catch (JsonException)
            {
                // Fall back to bounded text replacement for malformed diagnostic payloads.
            }
        }

        return SanitizeText(source, null, forceReplacement: false);
    }

    private string SanitizeJsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        try
        {
            var node = JsonNode.Parse(line);
            if (node is null)
            {
                return line;
            }

            SanitizeNode(node, null, sensitiveContentContext: false);
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            return SanitizeText(line, null, forceReplacement: false);
        }
    }

    private void SanitizeNode(JsonNode node, string? propertyName, bool sensitiveContentContext)
    {
        if (node is JsonObject jsonObject)
        {
            var objectIsSensitiveInput = IsSensitiveInputObject(jsonObject);
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Value is null || IsProtectedProperty(property.Key))
                {
                    continue;
                }

                var normalizedPropertyName = NormalizePropertyName(property.Key);
                var propertyIsSensitive = sensitiveProperties.Contains(normalizedPropertyName);
                var childSensitiveContext = sensitiveContentContext
                                            || propertyIsSensitive
                                            || objectIsSensitiveInput
                                               && sensitiveContentProperties.Contains(normalizedPropertyName);
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    if (!propertyIsSensitive
                        && !childSensitiveContext
                        && TrySanitizeEmbeddedJson(text, out var sanitizedJson))
                    {
                        jsonObject[property.Key] = sanitizedJson;
                        continue;
                    }

                    jsonObject[property.Key] = SanitizeText(
                        text,
                        property.Key,
                        propertyIsSensitive || childSensitiveContext);
                    continue;
                }

                SanitizeNode(
                    property.Value,
                    property.Key,
                    childSensitiveContext || objectIsSensitiveInput);
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    jsonArray[index] = SanitizeText(text, propertyName, sensitiveContentContext);
                }
                else if (jsonArray[index] is { } child)
                {
                    SanitizeNode(child, propertyName, sensitiveContentContext);
                }
            }
        }
    }

    private string SanitizeText(string value, string? propertyName, bool forceReplacement)
    {
        report.StringsScanned++;
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (forceReplacement)
        {
            if (!string.Equals(value, policy.Replacement, StringComparison.Ordinal))
            {
                report.RecordRedaction("sensitiveProperty");
                report.SensitivePropertiesRedacted++;
            }

            return policy.Replacement;
        }

        var result = value;
        foreach (var rule in rules)
        {
            var replacement = rule.Replacement ?? policy.Replacement;
            var matchCount = 0;
            result = rule.Pattern.Replace(result, match =>
            {
                if (rule.ShouldRedact is not null && !rule.ShouldRedact(match))
                {
                    return match.Value;
                }

                matchCount++;
                return replacement;
            });
            if (matchCount > 0)
            {
                report.RecordRedaction(rule.Id, matchCount);
            }
        }

        return result;
    }

    private bool TrySanitizeEmbeddedJson(string source, out string sanitized)
    {
        sanitized = source;
        var trimmed = source.Trim();
        if (trimmed.Length < 2
            || trimmed[0] is not ('{' or '[')
            || trimmed[^1] is not ('}' or ']'))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(trimmed);
            if (node is not (JsonObject or JsonArray))
            {
                return false;
            }

            SanitizeNode(node, null, sensitiveContentContext: false);
            sanitized = node.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<SessionSanitizationRegion>> FindSensitiveScreenshotRegions(
        AppSessionSnapshot snapshot)
    {
        var result = new Dictionary<string, IReadOnlyList<SessionSanitizationRegion>>(StringComparer.Ordinal);
        foreach (var frame in snapshot.Images)
        {
            var visualTrees = snapshot.VisualTreeSnapshots
                .Where(tree => string.Equals(tree.ScreenshotFrameId, frame.FrameId, StringComparison.Ordinal))
                .OrderByDescending(static tree => tree.CapturedAtUtc)
                .ToArray();
            if (visualTrees.Length == 0)
            {
                continue;
            }

            framesWithVisualTrees.Add(frame.FrameId);

            var regions = FindSensitiveRegions(visualTrees[0].Payload, frame.Width, frame.Height);
            if (regions.Count > 0)
            {
                result[frame.FrameId] = regions;
            }
        }

        return result;
    }

    private IReadOnlyList<SessionSanitizationRegion> FindSensitiveRegions(
        JsonObject payload,
        int imageWidth,
        int imageHeight)
    {
        var coordinateSpace = ReadCoordinateSpace(payload)
                              ?? ReadBounds(payload["root"] as JsonObject)
                              ?? new SessionSanitizationRegion(0, 0, imageWidth, imageHeight);
        var regions = new List<SessionSanitizationRegion>();
        VisitObjects(payload, candidate =>
        {
            if (!IsSensitiveInputObject(candidate) && !ContainsSensitiveContent(candidate))
            {
                return;
            }

            if (ReadBounds(candidate) is not { } bounds)
            {
                return;
            }

            var scaleX = coordinateSpace.Width <= 0 ? 1 : imageWidth / coordinateSpace.Width;
            var scaleY = coordinateSpace.Height <= 0 ? 1 : imageHeight / coordinateSpace.Height;
            regions.Add(new SessionSanitizationRegion(
                (bounds.X - coordinateSpace.X) * scaleX,
                (bounds.Y - coordinateSpace.Y) * scaleY,
                bounds.Width * scaleX,
                bounds.Height * scaleY));
        });
        return regions;
    }

    private bool ContainsSensitiveContent(JsonObject candidate)
    {
        foreach (var property in candidate)
        {
            if (property.Value is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (sensitiveProperties.Contains(NormalizePropertyName(property.Key))
                || ContainsSensitiveText(text))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsSensitiveText(string text)
        => rules.Any(rule => rule.Pattern.Matches(text)
            .Any(match => rule.ShouldRedact is null || rule.ShouldRedact(match)));

    private bool IsSensitiveInputObject(JsonObject candidate)
    {
        foreach (var property in candidate)
        {
            var normalizedName = NormalizePropertyName(property.Key);
            if (normalizedName is "ispassword" or "issecure" or "securetextentry"
                && property.Value is JsonValue booleanValue
                && booleanValue.TryGetValue<bool>(out var enabled)
                && enabled)
            {
                return true;
            }

            if (property.Value is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                continue;
            }

            if (normalizedName is not ("automationid" or "accessibilityidentifier" or "contenttype" or "id" or "name" or "role" or "type"))
            {
                continue;
            }

            var marker = NormalizePropertyName(text);
            if (!string.Equals(marker, "iphone", StringComparison.Ordinal)
                && sensitiveContextMarkers.Any(marker.Contains))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsProtectedProperty(string propertyName)
        => protectedProperties.Contains(NormalizePropertyName(propertyName));

    private static IReadOnlyList<SessionSanitizationCompiledRule> BuildRules(SessionSanitizationPolicy policy)
    {
        var enabled = new HashSet<string>(policy.Detectors, StringComparer.OrdinalIgnoreCase);
        var rules = new List<SessionSanitizationCompiledRule>();
        AddBuiltInRule(rules, enabled, "email", @"(?<![\w.+-])[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+");
        AddBuiltInRule(
            rules,
            enabled,
            "phone",
            @"(?<!\d)(?:\+\d[\d ()-]{7,}\d|\(\d{2,4}\)[\d ()-]{5,}\d|\d{2,4}(?:[ -]\d{2,4}){2,4})(?!\d)",
            IsLikelyPhoneNumber);
        AddBuiltInRule(rules, enabled, "ipAddress", @"(?<!\d)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?!\d)");
        AddBuiltInRule(
            rules,
            enabled,
            "creditCard",
            @"(?<!\d)(?:\d[ -]*?){13,19}(?!\d)",
            IsValidCreditCardNumber);
        AddBuiltInRule(
            rules,
            enabled,
            "credential",
            @"(?i)(?:bearer\s+[a-z0-9._~+/-]+=*|(?:api[_-]?key|access[_-]?token|auth(?:orization)?)[\s:=]+[^\s,;]+|sk-[a-z0-9_-]{12,})");

        foreach (var rule in policy.Rules)
        {
            var options = RegexOptions.CultureInvariant;
            if (rule.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            rules.Add(new SessionSanitizationCompiledRule(
                rule.Id.Trim(),
                new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(250)),
                string.IsNullOrEmpty(rule.Replacement) ? null : rule.Replacement));
        }

        return rules;
    }

    private static void AddBuiltInRule(
        ICollection<SessionSanitizationCompiledRule> rules,
        ISet<string> enabled,
        string id,
        string pattern,
        Func<Match, bool>? shouldRedact = null)
    {
        if (enabled.Contains(id))
        {
            rules.Add(new SessionSanitizationCompiledRule(
                id,
                new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)),
                null,
                shouldRedact));
        }
    }

    private static bool IsLikelyPhoneNumber(Match match)
    {
        var digitCount = match.Value.Count(char.IsDigit);
        return digitCount is >= 9 and <= 15;
    }

    private static bool IsValidCreditCardNumber(Match match)
    {
        var digits = match.Value.Where(char.IsDigit).Select(static character => character - '0').ToArray();
        if (digits.Length is < 13 or > 19 || digits.All(digit => digit == digits[0]))
        {
            return false;
        }

        var sum = 0;
        var doubleDigit = false;
        for (var index = digits.Length - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
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

    private static SessionSanitizationRegion? ReadCoordinateSpace(JsonObject payload)
        => ReadBounds(payload["coordinateSpace"] as JsonObject);

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

        result = 0;
        return false;
    }

    private static string NormalizePropertyName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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

    private static AppSessionSnapshot CloneWithArtifactSnapshots(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionArtifactSnapshot> artifactSnapshots)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, snapshotJsonOptions)?.AsObject()
                   ?? throw new InvalidDataException("The sanitized session snapshot could not be updated.");
        node["artifactSnapshots"] = JsonSerializer.SerializeToNode(artifactSnapshots, snapshotJsonOptions);
        return node.Deserialize<AppSessionSnapshot>(snapshotJsonOptions)
               ?? throw new InvalidDataException("The sanitized session snapshot could not be reconstructed.");
    }

    private static AppSessionSnapshot CloneWithImages(
        AppSessionSnapshot snapshot,
        IReadOnlyList<SessionImageFrame> images)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, snapshotJsonOptions)?.AsObject()
                   ?? throw new InvalidDataException("The sanitized session snapshot could not be updated.");
        node["images"] = JsonSerializer.SerializeToNode(images, snapshotJsonOptions);
        return node.Deserialize<AppSessionSnapshot>(snapshotJsonOptions)
               ?? throw new InvalidDataException("The sanitized session snapshot could not be reconstructed.");
    }
}

internal interface ISessionArchiveSanitizer : IDisposable
{
    SessionSanitizedCapture Sanitize(AppSessionSnapshot snapshot);

    bool TryWriteScreenshot(
        string sourceFilePath,
        SessionImageFrame frame,
        ZipArchive archive,
        string entryName,
        IReadOnlyList<SessionSanitizationRegion> sensitiveRegions);

    IReadOnlySet<string> WriteArtifacts(
        string artifactsDirectoryPath,
        ZipArchive archive,
        Func<string, string> buildEntryName,
        IReadOnlyDictionary<string, string>? sanitizedRelativePaths = null);
}

internal sealed record SessionSanitizationCompiledRule(
    string Id,
    Regex Pattern,
    string? Replacement,
    Func<Match, bool>? ShouldRedact = null);

internal sealed record SessionSanitizedCapture(
    AppSessionSnapshot Snapshot,
    SessionSanitizationPolicy Policy,
    SessionSanitizationReportBuilder Report,
    IReadOnlyDictionary<string, IReadOnlyList<SessionSanitizationRegion>> ScreenshotRegionsByFrameId);

internal readonly record struct SessionSanitizationRegion(
    double X,
    double Y,
    double Width,
    double Height);

internal sealed class SessionSanitizationReportBuilder
{
    private readonly Dictionary<string, int> redactionsByRule = new(StringComparer.Ordinal);

    public SessionSanitizationReportBuilder(string policyId)
    {
        PolicyId = policyId;
    }

    public string PolicyId { get; }

    public int StringsScanned { get; set; }

    public int RedactedStrings { get; private set; }

    public int SensitivePropertiesRedacted { get; set; }

    public int ScreenshotsRedacted { get; set; }

    public int ScreenshotsRemoved { get; set; }

    public int ScreenshotRegionsRedacted { get; set; }

    public int ArtifactsSanitized { get; set; }

    public int ArtifactsExcluded { get; set; }

    public void RecordRedaction(string ruleId, int count = 1)
    {
        RedactedStrings++;
        redactionsByRule[ruleId] = redactionsByRule.GetValueOrDefault(ruleId) + count;
    }

    public SessionSanitizationReport Build()
    {
        return new SessionSanitizationReport
        {
            PolicyId = PolicyId,
            SanitizedAtUtc = DateTimeOffset.UtcNow,
            StringsScanned = StringsScanned,
            RedactedStrings = RedactedStrings,
            SensitivePropertiesRedacted = SensitivePropertiesRedacted,
            ScreenshotsRedacted = ScreenshotsRedacted,
            ScreenshotsRemoved = ScreenshotsRemoved,
            ScreenshotRegionsRedacted = ScreenshotRegionsRedacted,
            ArtifactsSanitized = ArtifactsSanitized,
            ArtifactsExcluded = ArtifactsExcluded,
            RedactionsByRule = new Dictionary<string, int>(redactionsByRule, StringComparer.Ordinal)
        };
    }
}
