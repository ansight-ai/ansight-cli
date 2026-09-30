using System.Text.Json;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class RepositoryTaskRunStore
{
    private const int MaximumRetainedRuns = 2_000;
    private const long MaximumFileBytesBeforeCompaction = 16 * 1_024 * 1_024;
    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions json = new(JsonUtil.Compact);

    public RepositoryTaskRunStore(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        var directoryPath = Path.Combine(applicationDataPath, "automation");
        Directory.CreateDirectory(directoryPath);
        filePath = Path.Combine(directoryPath, "task-runs.jsonl");
    }

    public void Append(RepositoryTaskRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = JsonSerializer.Serialize(result, json);
        lock (gate)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
            if (new FileInfo(filePath).Length > MaximumFileBytesBeforeCompaction)
            {
                Compact();
            }
        }
    }

    private void Compact()
    {
        var retained = ReadAll()
            .OrderByDescending(item => item.CompletedAtUtc)
            .Take(MaximumRetainedRuns)
            .OrderBy(item => item.CompletedAtUtc)
            .ToArray();
        var temporaryPath = filePath + ".tmp";
        File.WriteAllLines(
            temporaryPath,
            retained.Select(item => JsonSerializer.Serialize(item, json)));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private IReadOnlyList<RepositoryTaskRunResult> ReadAll()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        var results = new List<RepositoryTaskRunResult>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var result = JsonSerializer.Deserialize<RepositoryTaskRunResult>(line, json);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
            catch (JsonException)
            {
                // Preserve later valid runs when a process exit left one partial JSONL line.
            }
        }

        return results;
    }
}
