using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Ansight.Host.Files;

internal static class ArtifactDatabaseComparer
{
    internal static async Task<ArtifactFileDiff> CompareAsync(string beforePath, string afterPath, string path, ArtifactDiffRequest request, CancellationToken cancellationToken)
    {
        var changes = new List<ArtifactChange>();
        try
        {
            FileVisualizationService.EnsureSqliteProviderInitialized();
            var before = await ReadAsync(beforePath, cancellationToken).ConfigureAwait(false);
            var after = await ReadAsync(afterPath, cancellationToken).ConfigureAwait(false);
            CompareRows(before, after, changes, cancellationToken);
            return ArtifactFileComparer.Page(path, "sqlite", changes, request, true,
                "Rows use declared primary keys when unique and non-null; otherwise duplicate-aware full-row matching. Schema includes tables, indexes, views, and triggers.");
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArtifactFileComparer.ChangeLimitException)
        {
            return ArtifactFileComparer.Page(path, "sqlite", changes, request, false,
                exception is ArtifactFileComparer.ChangeLimitException ? "Comparison exceeded 20,000 changes." : exception.Message);
        }
    }

    private static void CompareRows(JsonObject before, JsonObject after, List<ArtifactChange> changes, CancellationToken cancellationToken)
    {
        ArtifactFileComparer.CompareJson(before["schema"], after["schema"], "/schema", changes, null, cancellationToken);
        var beforeTables = (JsonObject)before["tables"]!;
        var afterTables = (JsonObject)after["tables"]!;
        foreach (var table in beforeTables.Select(entry => entry.Key).Union(afterTables.Select(entry => entry.Key)).Order(StringComparer.Ordinal))
        {
            var left = beforeTables[table] as JsonObject;
            var right = afterTables[table] as JsonObject;
            // Choose one matching strategy for both snapshots, even if key usability changed.
            var keyed = (left is null || left["matching"]!.GetValue<string>() == "primary-key")
                && (right is null || right["matching"]!.GetValue<string>() == "primary-key")
                && (left is null || right is null || JsonNode.DeepEquals(left["keyColumns"], right["keyColumns"]));
            var leftRows = Rows(left, keyed);
            var rightRows = Rows(right, keyed);
            foreach (var identity in leftRows.Select(entry => entry.Key).Union(rightRows.Select(entry => entry.Key)).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftValue = leftRows[identity];
                var rightValue = rightRows[identity];
                if (JsonNode.DeepEquals(leftValue, rightValue)) continue;
                var leftCount = keyed ? (leftValue is null ? 0 : 1) : leftValue?.GetValue<int>() ?? 0;
                var rightCount = keyed ? (rightValue is null ? 0 : 1) : rightValue?.GetValue<int>() ?? 0;
                var leftRow = keyed ? leftValue as JsonObject : leftCount > 0 ? JsonNode.Parse(identity)!.AsObject() : null;
                var rightRow = keyed ? rightValue as JsonObject : rightCount > 0 ? JsonNode.Parse(identity)!.AsObject() : null;
                var columns = (leftRow?.Select(entry => entry.Key) ?? []).Union(rightRow?.Select(entry => entry.Key) ?? [])
                    .Where(column => leftRow is null || rightRow is null || leftRow.ContainsKey(column) != rightRow.ContainsKey(column) || !JsonNode.DeepEquals(leftRow[column], rightRow[column]))
                    .Order(StringComparer.Ordinal).ToArray();
                var key = keyed ? string.Join(", ", ((JsonArray)(left ?? right)!["keyColumns"]!).Select(column =>
                    column!.GetValue<string>() + "=" + Display((leftRow ?? rightRow)![column.GetValue<string>()]))) : "Full-row match";
                var kind = leftCount == 0 || rightCount > leftCount ? "added" : rightCount == 0 || leftCount > rightCount ? "removed" : "modified";
                changes.Add(new ArtifactChange($"{table} [{key}]", kind, Describe(leftRow, leftCount), Describe(rightRow, rightCount),
                    new(table, key, columns, leftRow, rightRow, leftCount, rightCount)));
                if (changes.Count > 20_000) throw new ArtifactFileComparer.ChangeLimitException();
            }
        }
    }

    private static JsonObject Rows(JsonObject? table, bool keyed)
    {
        if (table is null) return new JsonObject();
        var rows = (JsonObject)table["rows"]!;
        if (keyed || table["matching"]!.GetValue<string>() != "primary-key") return rows;
        var counts = new JsonObject();
        foreach (var row in rows)
        {
            var identity = row.Value!.ToJsonString();
            counts[identity] = (counts[identity]?.GetValue<int>() ?? 0) + 1;
        }
        return counts;
    }

    private static string? Describe(JsonObject? row, int count) => row is null ? null
        : (count > 1 ? $"{count} occurrences\n" : "") + string.Join("\n", row.Select(cell => cell.Key + ": " + Display(cell.Value)));

    private static string Display(JsonNode? cell) => cell is null ? "NULL"
        : cell["type"]?.GetValue<string>() == "blob" ? $"BLOB ({cell["length"]} bytes, SHA256 {cell["sha256"]})"
        : cell["type"]?.GetValue<string>() == "text" ? System.Text.Json.JsonSerializer.Serialize(cell["value"]!.GetValue<string>())
        : cell["value"]!.GetValue<string>();

    private static async Task<JsonObject> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = new JsonObject();
        var schema = new JsonObject();
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, type, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' OR name = 'sqlite_sequence' ORDER BY type, name";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0); var kind = reader.GetString(1);
                schema[$"{kind}:{name}"] = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (kind == "table") tables.Add(name);
            }
        }
        result["schema"] = schema;
        var data = new JsonObject(); result["tables"] = data;
        var totalRows = 0;
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keys = new SortedDictionary<long, string>();
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT name, pk FROM pragma_table_xinfo($table) WHERE pk > 0 ORDER BY pk";
                columns.Parameters.AddWithValue("$table", table);
                await using var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) keys[reader.GetInt64(1)] = reader.GetString(0);
            }
            var rows = new List<JsonObject>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (++totalRows > 100_000) throw new InvalidDataException("Database comparison exceeds 100,000 rows; no complete row comparison is available.");
                    var row = new JsonObject();
                    // Preserve storage types and exact integers. BLOBs compare by full-content hash.
                    foreach (var index in Enumerable.Range(0, reader.FieldCount).OrderBy(reader.GetName, StringComparer.Ordinal))
                        row[reader.GetName(index)] = Cell(reader.GetValue(index));
                    rows.Add(row);
                }
            }
            var keyed = new JsonObject();
            var validKey = keys.Count > 0;
            foreach (var row in rows)
            {
                var keyCells = keys.Values.Select(key => row[key]).ToArray();
                if (keyCells.Any(cell => cell is null)) { validKey = false; break; }
                var key = new JsonArray(keyCells.Select(cell => cell!.DeepClone()).ToArray()).ToJsonString();
                if (keyed.ContainsKey(key)) { validKey = false; break; }
                keyed[key] = row.DeepClone();
            }
            if (validKey) data[table] = new JsonObject { ["matching"] = "primary-key", ["keyColumns"] = new JsonArray(keys.Values.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()), ["rows"] = keyed };
            else
            {
                var counts = new JsonObject();
                foreach (var row in rows)
                {
                    var key = row.ToJsonString();
                    counts[key] = (counts[key]?.GetValue<int>() ?? 0) + 1;
                }
                data[table] = new JsonObject { ["matching"] = "full-row-multiset", ["rows"] = counts };
            }
        }
        return result;
    }

    private static JsonNode? Cell(object value) => value switch
    {
        DBNull => null,
        byte[] bytes => new JsonObject { ["type"] = "blob", ["length"] = bytes.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) },
        long integer => new JsonObject { ["type"] = "integer", ["value"] = integer.ToString(CultureInfo.InvariantCulture) },
        double number => new JsonObject { ["type"] = "real", ["value"] = number.ToString("R", CultureInfo.InvariantCulture) },
        _ => new JsonObject { ["type"] = "text", ["value"] = value.ToString() }
    };
}
