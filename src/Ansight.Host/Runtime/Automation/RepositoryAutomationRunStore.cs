using System.Text.Json;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationRunStore
{
    private const int MaximumRetainedAttempts = 2_000;
    private const long MaximumFileBytesBeforeCompaction = 16 * 1_024 * 1_024;
    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions json = new(JsonUtil.Compact);

    public RepositoryAutomationRunStore(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        var directoryPath = Path.Combine(applicationDataPath, "automation");
        Directory.CreateDirectory(directoryPath);
        filePath = Path.Combine(directoryPath, "trigger-runs.jsonl");
    }

    public void Append(AutomationRunCompletedEvent completedEvent)
    {
        ArgumentNullException.ThrowIfNull(completedEvent);
        var line = JsonSerializer.Serialize(completedEvent, json);
        lock (gate)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
            if (new FileInfo(filePath).Length > MaximumFileBytesBeforeCompaction)
            {
                Compact();
            }
        }
    }

    public IReadOnlyList<AutomationRunCompletedEvent> GetRecent(string appId, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Run trace limit must be between 1 and 500.");
        }

        lock (gate)
        {
            return ReadAll()
                .Where(item => string.Equals(item.AppId, appId.Trim(), StringComparison.Ordinal))
                .OrderByDescending(item => item.CompletedAtUtc)
                .Take(limit)
                .ToArray();
        }
    }

    private void Compact()
    {
        var retained = ReadAll()
            .OrderByDescending(item => item.CompletedAtUtc)
            .Take(MaximumRetainedAttempts)
            .OrderBy(item => item.CompletedAtUtc)
            .ToArray();
        var temporaryPath = filePath + ".tmp";
        File.WriteAllLines(
            temporaryPath,
            retained.Select(item => JsonSerializer.Serialize(item, json)));
        File.Move(temporaryPath, filePath, overwrite: true);
    }

    private IReadOnlyList<AutomationRunCompletedEvent> ReadAll()
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<AutomationRunCompletedEvent>();
        }

        var results = new List<AutomationRunCompletedEvent>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var item = JsonSerializer.Deserialize<AutomationRunCompletedEvent>(line, json);
                if (item is not null)
                {
                    results.Add(item);
                }
            }
            catch (JsonException)
            {
                // Preserve later valid traces when a process exit left one partial JSONL line.
            }
        }

        return results;
    }
}
