using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class RepositoryTaskRunStore
{
    private const int MaximumRetainedRuns = 2_000;
    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions json = new(JsonUtil.Compact);

    public RepositoryTaskRunStore(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        var directoryPath = Path.Combine(applicationDataPath, "automation");
        PrivateStorageDirectory.Ensure(directoryPath);
        filePath = Path.Combine(directoryPath, "task-runs.jsonl");
        BoundedRunHistory.RestrictFile(filePath);
        if (File.Exists(filePath) && new FileInfo(filePath).Length > BoundedRunHistory.MaximumFileBytes)
        {
            Compact();
        }
    }

    public void Append(RepositoryTaskRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = SerializeBounded(result);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > BoundedRunHistory.MaximumRecordBytes)
        {
            throw new IOException("Repository task history record exceeds the local size limit.");
        }
        lock (gate)
        {
            File.AppendAllText(filePath, line + Environment.NewLine);
            BoundedRunHistory.RestrictFile(filePath);
            if (new FileInfo(filePath).Length > BoundedRunHistory.MaximumFileBytes)
            {
                Compact();
            }
        }
    }

    private void Compact()
    {
        BoundedRunHistory.Compact(
            filePath,
            ReadAll(),
            static item => item.CompletedAtUtc,
            SerializeBounded,
            MaximumRetainedRuns);
    }

    private string SerializeBounded(RepositoryTaskRunResult result)
    {
        var input = result.Input;
        if (JsonSerializer.SerializeToUtf8Bytes(input, json).Length > 32 * 1_024)
        {
            input = new JsonObject { ["truncated"] = true };
        }

        var output = result.Output;
        if (output is not null && JsonSerializer.SerializeToUtf8Bytes(output, json).Length > 32 * 1_024)
        {
            output = new JsonObject { ["truncated"] = true };
        }

        var bounded = result with
        {
            Input = input,
            Output = output,
            Message = BoundedRunHistory.LimitText(result.Message, 4 * 1_024),
            StandardError = BoundedRunHistory.LimitText(result.StandardError, 8 * 1_024)
        };
        var line = JsonSerializer.Serialize(bounded, json);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > BoundedRunHistory.MaximumRecordBytes)
        {
            bounded = bounded with
            {
                Input = new JsonObject { ["truncated"] = true },
                Output = new JsonObject { ["truncated"] = true },
                ToolCalls = [],
                SourceTrace = null
            };
            line = JsonSerializer.Serialize(bounded, json);
        }

        return line;
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
