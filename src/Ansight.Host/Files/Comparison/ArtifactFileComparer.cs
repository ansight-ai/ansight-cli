using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Files;

internal static class ArtifactFileComparer
{
    private const long MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumChanges = 20_000;

    internal static async Task<ArtifactFileDiff> CompareAsync(string beforePath, string afterPath, string path, string format, ArtifactDiffRequest request, CancellationToken cancellationToken)
    {
        var beforeHash = await HashAsync(beforePath, cancellationToken).ConfigureAwait(false);
        var afterHash = await HashAsync(afterPath, cancellationToken).ConfigureAwait(false);
        if (beforeHash == afterHash) return new(path, format, "identical", true, true, null, 0, [], null);
        if (new FileInfo(beforePath).Length > MaximumFileBytes || new FileInfo(afterPath).Length > MaximumFileBytes)
            return new(path, format, "changed", false, false, "Bytes differ. Detailed comparison is limited to 32 MiB per file; no partial content was compared.", 0, [], null);
        if (request.Mode != "text" && format == "sqlite")
            return await ArtifactDatabaseComparer.CompareAsync(beforePath, afterPath, path, request, cancellationToken).ConfigureAwait(false);
        if (request.Mode != "text" && format == "zip")
            return await ArtifactArchiveComparer.CompareAsync(beforePath, afterPath, path, request, cancellationToken).ConfigureAwait(false);
        var before = Decode(await File.ReadAllBytesAsync(beforePath, cancellationToken).ConfigureAwait(false));
        var after = Decode(await File.ReadAllBytesAsync(afterPath, cancellationToken).ConfigureAwait(false));
        if (before is null || after is null)
            return new(path, format, "changed", false, true, $"Binary content differs. SHA-256: {beforeHash} → {afterHash}. No semantic comparer is available.", 0, [], null);
        var changes = new List<ArtifactChange>();
        try
        {
            if (request.Mode != "text" && format == "json")
                CompareJson(JsonNode.Parse(before), JsonNode.Parse(after), "", changes, request.ArrayKey, cancellationToken);
            else if (request.Mode != "text" && format == "xml")
                CompareJson(XmlTree(before), XmlTree(after), "", changes, null, cancellationToken);
            else if (request.Mode != "text" && format is "csv" or "tsv" && !string.IsNullOrEmpty(request.ArrayKey))
            {
                var oldRows = ParseTable(before, format == "tsv" ? '\t' : ',');
                var newRows = ParseTable(after, format == "tsv" ? '\t' : ',');
                if (!oldRows.Headers.Contains(request.ArrayKey, StringComparer.Ordinal) || !newRows.Headers.Contains(request.ArrayKey, StringComparer.Ordinal))
                    throw new ArgumentException($"Table key column '{request.ArrayKey}' was not found.");
                CompareJson(oldRows.AsJson(), newRows.AsJson(), "", changes, request.ArrayKey, cancellationToken);
            }
            else CompareLines(before, after, changes, request.IgnoreWhitespace, cancellationToken);
        }
        catch (ChangeLimitException)
        {
            return Page(path, format, changes, request, false, "Comparison exceeded 20,000 changes; results are incomplete.");
        }
        return Page(path, request.Mode == "text" ? "text" : format, changes, request, true, null);
    }

    internal static ArtifactFileDiff Page(string path, string format, IReadOnlyList<ArtifactChange> changes, ArtifactDiffRequest request, bool complete, string? message)
    {
        var page = changes.Skip(request.Offset).Take(request.Limit).ToArray();
        return new(path, format, changes.Count == 0 && complete ? "equivalent" : "changed", false, complete, message,
            changes.Count, page, request.Offset + page.Length < changes.Count ? request.Offset + page.Length : null);
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string? Decode(byte[] bytes)
    {
        try
        {
            string text;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            else text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');
            return text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    internal static void CompareJson(JsonNode? before, JsonNode? after, string path, List<ArtifactChange> changes, string? arrayKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject left && after is JsonObject right)
        {
            foreach (var key in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var childPath = path + "/" + Escape(key);
                if (!left.ContainsKey(key)) Add(changes, new(childPath, "added", null, Render(right[key])));
                else if (!right.ContainsKey(key)) Add(changes, new(childPath, "removed", Render(left[key]), null));
                else CompareJson(left[key], right[key], childPath, changes, arrayKey, cancellationToken);
            }
        }
        else if (before is JsonArray oldArray && after is JsonArray newArray)
        {
            if (!string.IsNullOrWhiteSpace(arrayKey) && oldArray.Concat(newArray).All(node => node is JsonObject obj && obj[arrayKey] is JsonValue))
            {
                CompareJson(KeyArray(oldArray, arrayKey), KeyArray(newArray, arrayKey), path, changes, arrayKey, cancellationToken);
                return;
            }
            for (var index = 0; index < Math.Max(oldArray.Count, newArray.Count); index++)
            {
                if (index >= oldArray.Count) Add(changes, new($"{path}/{index}", "added", null, Render(newArray[index])));
                else if (index >= newArray.Count) Add(changes, new($"{path}/{index}", "removed", Render(oldArray[index]), null));
                else CompareJson(oldArray[index], newArray[index], $"{path}/{index}", changes, arrayKey, cancellationToken);
            }
        }
        else Add(changes, new(path.Length == 0 ? "/" : path, "modified", Render(before), Render(after)));
    }

    private static JsonObject KeyArray(JsonArray array, string key)
    {
        var result = new JsonObject();
        foreach (var node in array)
        {
            var identity = Render(node![key]);
            if (result.ContainsKey(identity)) throw new ArgumentException($"Array key '{key}' is not unique ({identity}). Choose a unique key or omit --array-key.");
            result.Add(identity, node.DeepClone());
        }
        return result;
    }

    private static JsonNode XmlTree(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        return Element(document.Root ?? throw new ArgumentException("XML has no root element."));
    }

    private static JsonObject Element(XElement element, int depth = 0)
    {
        if (depth > 256) throw new ArgumentException("XML exceeds the comparison depth limit (256).");
        var attributes = new JsonObject();
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)) attributes[attribute.Name.ToString()] = attribute.Value;
        var nodes = new JsonArray();
        foreach (var node in element.Nodes())
        {
            if (node is XElement child) nodes.Add(Element(child, depth + 1));
            else if (node is XText text) nodes.Add(JsonValue.Create(text.Value));
            else if (node is XComment comment) nodes.Add(new JsonObject { ["comment"] = comment.Value });
            else if (node is XProcessingInstruction instruction) nodes.Add(new JsonObject { ["instruction"] = instruction.Target, ["data"] = instruction.Data });
        }
        return new() { ["name"] = element.Name.ToString(), ["attributes"] = attributes, ["nodes"] = nodes };
    }

    private static ParsedTable ParseTable(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (!quoted && (character == separator || character is '\r' or '\n'))
            {
                row.Add(field.ToString()); field.Clear();
                if (character != separator)
                {
                    rows.Add(row); row = [];
                    if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                }
            }
            else field.Append(character);
        }
        if (quoted) throw new ArgumentException("Unterminated quoted table field.");
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        if (rows.Count == 0) return new([], []);
        var headers = rows[0];
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Count) throw new ArgumentException("Table headers must be unique.");
        var result = new JsonArray();
        foreach (var cells in rows.Skip(1))
        {
            if (cells.Count != headers.Count) throw new ArgumentException("Table row length does not match its headers.");
            var item = new JsonObject();
            for (var index = 0; index < headers.Count; index++) item[headers[index]] = cells[index];
            result.Add(item);
        }
        return new(headers, result);
    }

    private sealed record ParsedTable(IReadOnlyList<string> Headers, JsonArray Rows)
    {
        public JsonObject AsJson() => new()
        {
            ["columns"] = new JsonArray(Headers.Select(header => (JsonNode?)JsonValue.Create(header)).ToArray()),
            ["rows"] = Rows
        };
    }

    private static void CompareLines(string before, string after, List<ArtifactChange> changes, bool ignoreWhitespace, CancellationToken cancellationToken)
    {
        // Keep line endings in the comparison unless normalization was explicitly requested.
        var left = before.Split('\n'); var right = after.Split('\n');
        string Normalize(string value) => ignoreWhitespace ? string.Concat(value.Where(character => !char.IsWhiteSpace(character))) : value;
        var oldLines = left.Select(Normalize).ToArray(); var newLines = right.Select(Normalize).ToArray();
        var prefix = 0;
        while (prefix < left.Length && prefix < right.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var oldEnd = left.Length; var newEnd = right.Length;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1]) { oldEnd--; newEnd--; }
        var oldCount = oldEnd - prefix; var newCount = newEnd - prefix;
        // A bounded LCS finds stable lines. For huge edits use a valid non-minimal replacement block.
        if ((long)oldCount * newCount > 2_000_000)
        {
            Add(changes, new($"lines {prefix + 1}–{oldEnd} → {prefix + 1}–{newEnd}", "modified", string.Join('\n', left[prefix..oldEnd]), string.Join('\n', right[prefix..newEnd])));
            return;
        }
        var lengths = new int[oldCount + 1, newCount + 1];
        for (var i = oldCount - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var j = newCount - 1; j >= 0; j--)
                lengths[i, j] = oldLines[prefix + i] == newLines[prefix + j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        }
        var firstChange = changes.Count;
        var x = 0; var y = 0;
        while (x < oldCount || y < newCount)
        {
            if (x < oldCount && y < newCount && oldLines[prefix + x] == newLines[prefix + y]) { x++; y++; }
            else if (x < oldCount && (y == newCount || lengths[x + 1, y] >= lengths[x, y + 1]))
            { Add(changes, new($"line {prefix + x + 1}", "removed", left[prefix + x], null)); x++; }
            else { Add(changes, new($"line {prefix + y + 1}", "added", null, right[prefix + y])); y++; }
        }
        // Pair adjacent replacement lines for before/after and word-span presentation.
        for (var index = firstChange; index + 1 < changes.Count; index++)
        {
            if (changes[index].Kind != "removed" || changes[index + 1].Kind != "added") continue;
            changes[index] = new(changes[index].Path + " → " + changes[index + 1].Path, "modified", changes[index].Before, changes[index + 1].After);
            changes.RemoveAt(index + 1);
        }
    }

    internal static void Add(List<ArtifactChange> changes, ArtifactChange change)
    {
        if (changes.Count >= MaximumChanges) throw new ChangeLimitException();
        changes.Add(change);
    }
    internal static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static string Render(JsonNode? node) => node?.ToJsonString() ?? "null";
    internal sealed class ChangeLimitException : Exception;
}
