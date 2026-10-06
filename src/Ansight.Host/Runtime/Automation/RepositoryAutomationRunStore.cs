using System.Text.Json;

namespace Ansight.Host.Runtime.Automation;

internal sealed class RepositoryAutomationRunStore
{
    private const int MaximumRetainedAttempts = 2_000;
    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly JsonSerializerOptions json = new(JsonUtil.Compact);

    public RepositoryAutomationRunStore(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        var directoryPath = Path.Combine(applicationDataPath, "automation");
        PrivateStorageDirectory.Ensure(directoryPath);
        filePath = Path.Combine(directoryPath, "trigger-runs.jsonl");
        BoundedRunHistory.RestrictFile(filePath);
        if (File.Exists(filePath) && new FileInfo(filePath).Length > BoundedRunHistory.MaximumFileBytes)
        {
            Compact();
        }
    }

    public void Append(AutomationRunCompletedEvent completedEvent)
    {
        ArgumentNullException.ThrowIfNull(completedEvent);
        var line = SerializeBounded(completedEvent);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > BoundedRunHistory.MaximumRecordBytes)
        {
            throw new IOException("Automation run history record exceeds the local size limit.");
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
        BoundedRunHistory.Compact(
            filePath,
            ReadAll(),
            static item => item.CompletedAtUtc,
            SerializeBounded,
            MaximumRetainedAttempts);
    }

    private string SerializeBounded(AutomationRunCompletedEvent completedEvent)
    {
        var output = completedEvent.Output;
        if (output is not null && JsonSerializer.SerializeToUtf8Bytes(output, json).Length > 32 * 1_024)
        {
            output = new System.Text.Json.Nodes.JsonObject { ["truncated"] = true };
        }

        var matchedEvent = completedEvent.MatchedEvent;
        var payload = matchedEvent.Payload;
        if (JsonSerializer.SerializeToUtf8Bytes(payload, json).Length > 16 * 1_024)
        {
            matchedEvent = new AutomationEventEnvelope
            {
                EventId = matchedEvent.EventId,
                Kind = matchedEvent.Kind,
                OccurredAtUtc = matchedEvent.OccurredAtUtc,
                AppId = matchedEvent.AppId,
                SessionId = matchedEvent.SessionId,
                CorrelationId = matchedEvent.CorrelationId,
                CausationId = matchedEvent.CausationId,
                Payload = new System.Text.Json.Nodes.JsonObject { ["truncated"] = true }
            };
        }

        var bounded = completedEvent with
        {
            Output = output,
            MatchedEvent = matchedEvent,
            Message = BoundedRunHistory.LimitText(completedEvent.Message, 4 * 1_024),
            StandardError = BoundedRunHistory.LimitText(completedEvent.StandardError, 8 * 1_024)
        };
        var line = JsonSerializer.Serialize(bounded, json);
        if (System.Text.Encoding.UTF8.GetByteCount(line) > BoundedRunHistory.MaximumRecordBytes)
        {
            bounded = bounded with
            {
                Output = null,
                StandardError = string.Empty,
                MatchedEvent = new AutomationEventEnvelope
                {
                    EventId = matchedEvent.EventId,
                    Kind = matchedEvent.Kind,
                    OccurredAtUtc = matchedEvent.OccurredAtUtc,
                    AppId = matchedEvent.AppId,
                    SessionId = matchedEvent.SessionId,
                    CorrelationId = matchedEvent.CorrelationId,
                    CausationId = matchedEvent.CausationId
                }
            };
            line = JsonSerializer.Serialize(bounded, json);
        }

        return line;
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
