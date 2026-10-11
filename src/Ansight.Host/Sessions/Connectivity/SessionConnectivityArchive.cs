using System.IO.Compression;
using System.Text.Json;

namespace Ansight.Host.Connectivity;

internal static class SessionConnectivityArchive
{
    internal sealed record Entry(string Path, string Content);

    public static IReadOnlyList<Entry> Entries(IReadOnlyList<ConnectivityRecord> records)
    {
        if (records.Count == 0) return [];
        var files = new List<Entry>();
        var segments = new List<object>();
        for (var start = 0; start < records.Count; start += 256)
        {
            var chunk = records.Skip(start).Take(256).ToArray();
            var path = $"segments/{start / 256 + 1:D6}.jsonl";
            var content = string.Join('\n', (start == 0 ? chunk : new[] { records[start - 1] }.Concat(chunk)).Select(ConnectivityJson.Serialize)) + "\n";
            files.Add(new Entry("connectivity/" + path, content));
            segments.Add(new { path, fromUtc = chunk[0].CapturedAtUtc, toUtc = chunk[^1].CapturedAtUtc, recordCount = chunk.Length + (start > 0 ? 1 : 0) });
        }
        files.Add(new Entry("connectivity/index.json", JsonSerializer.Serialize(new
        {
            schema = "ansight.connectivity-index.v1", retainedRecordCount = records.Count,
            stateChanges = records.Where((value, index) => value.Kind != "quality" || index == 0 || records[index - 1].Kind == "gap").ToArray(),
            supportedMeasurements = records.SelectMany(value => value.SupportedMeasurements).Distinct().Order().ToArray(), segments
        }, ConnectivityJson.Options)));
        return files;
    }

    public static void WriteZip(ZipArchive archive, IReadOnlyList<ConnectivityRecord> records)
    {
        foreach (var file in Entries(records))
        {
            var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(file.Content);
        }
    }

    public static void WriteDirectory(string sessionDirectory, IReadOnlyList<ConnectivityRecord> records)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Entries(records))
        {
            var path = Path.Combine(sessionDirectory, file.Path);
            expected.Add(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && File.ReadAllText(path) == file.Content) continue;
            File.WriteAllText(path + ".tmp", file.Content);
            File.Move(path + ".tmp", path, true);
        }
        var root = Path.Combine(sessionDirectory, "connectivity");
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            if (!expected.Contains(path) && (path.EndsWith(".jsonl", StringComparison.Ordinal) || path == Path.Combine(root, "index.json"))) File.Delete(path);
    }

    public static IEnumerable<SessionApplicationEvent> ReadDirectory(string sessionDirectory)
    {
        var root = Path.Combine(sessionDirectory, "connectivity", "segments");
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl").Order(StringComparer.Ordinal))
            foreach (var line in File.ReadLines(path))
                if (ConnectivityJson.Parse(line) is { } record) yield return SessionConnectivity.ToEvent(record);
    }
}
