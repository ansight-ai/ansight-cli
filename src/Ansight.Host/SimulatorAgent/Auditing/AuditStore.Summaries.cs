using System.Text.Json;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal sealed partial class AuditStore
{
    private readonly object summaryGate = new();
    private readonly Dictionary<string, CachedAuditSummary> summariesByPath = new(StringComparer.Ordinal);

    public IReadOnlyList<SimulatorAgentRunSummary> ListSummaries()
    {
        lock (summaryGate)
        {
            try
            {
                if (!Directory.Exists(auditDirectoryPath))
                {
                    summariesByPath.Clear();

                    return [];
                }

                var paths = Directory.GetFiles(auditDirectoryPath, "*.json", SearchOption.TopDirectoryOnly);
                var retainedPaths = paths.ToHashSet(StringComparer.Ordinal);

                foreach (var removedPath in summariesByPath.Keys.Where(path => !retainedPaths.Contains(path)).ToArray())
                {
                    summariesByPath.Remove(removedPath);
                }

                var summaries = new List<SimulatorAgentRunSummary>();

                foreach (var path in paths)
                {
                    var summary = ReadSummary(path);

                    if (summary is not null)
                    {
                        summaries.Add(summary);
                    }
                }

                return summaries
                    .OrderByDescending(static summary => summary.StartedUtc)
                    .ThenByDescending(static summary => summary.RunId, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public SimulatorAgentRunHistoryEntry? Load(SimulatorAgentRunSummary summary)
    {
        return ReadEntry(summary.FilePath);
    }

    private SimulatorAgentRunSummary? ReadSummary(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var length = file.Length;
            var lastWriteTimeUtc = file.LastWriteTimeUtc;
            var isUnchanged = summariesByPath.TryGetValue(path, out var cached)
                && cached.Length == length
                && cached.LastWriteTimeUtc == lastWriteTimeUtc;

            if (isUnchanged)
            {
                return cached!.Summary;
            }

            SimulatorAgentRunSummary? summary = null;

            try
            {
                using var stream = File.OpenRead(path);
                summary = JsonSerializer.Deserialize<SimulatorAgentRunSummary>(stream, jsonOptions);

                if (summary is not null)
                {
                    summary = summary with { FilePath = path };
                }
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                // Retry malformed audits only when their file changes.
            }

            summariesByPath[path] = new CachedAuditSummary(length, lastWriteTimeUtc, summary);

            return summary;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            summariesByPath.Remove(path);

            return null;
        }
    }

    private sealed record CachedAuditSummary(
        long Length,
        DateTime LastWriteTimeUtc,
        SimulatorAgentRunSummary? Summary);
}
