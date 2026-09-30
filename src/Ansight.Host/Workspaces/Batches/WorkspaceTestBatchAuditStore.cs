using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.Batches;

internal sealed record WorkspaceTestBatchAuditSaveResult(string? FilePath, string? ErrorMessage);

internal sealed class WorkspaceTestBatchAuditStore
{
    private const string HistoryDirectoryName = "workspace-test-history";
    private readonly JsonSerializerOptions jsonOptions = new(JsonUtil.Pretty)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public WorkspaceTestBatchAuditStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        HistoryDirectoryPath = Path.Combine(
            applicationPaths.ApplicationDataPath,
            HistoryDirectoryName,
            "batches");
    }

    public string HistoryDirectoryPath { get; }

    public WorkspaceTestBatchAuditSaveResult Save(WorkspaceTestBatchRunAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        try
        {
            Directory.CreateDirectory(HistoryDirectoryPath);
            var timestamp = audit.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
            var filePath = Path.Combine(
                HistoryDirectoryPath,
                $"{timestamp}-{NormalizeRunId(audit.BatchRunId)}.json");
            var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(audit, jsonOptions));
                TryRestrictFilePermissions(temporaryPath);
                File.Move(temporaryPath, filePath, overwrite: true);
                TryRestrictFilePermissions(filePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new WorkspaceTestBatchAuditSaveResult(filePath, null);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or NotSupportedException)
        {
            return new WorkspaceTestBatchAuditSaveResult(null, exception.Message);
        }
    }

    public IReadOnlyList<WorkspaceTestBatchRunHistoryEntry> List()
    {
        try
        {
            if (!Directory.Exists(HistoryDirectoryPath))
            {
                return [];
            }

            var entries = new List<WorkspaceTestBatchRunHistoryEntry>();
            foreach (var filePath in Directory.EnumerateFiles(
                         HistoryDirectoryPath,
                         "*.json",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var audit = JsonSerializer.Deserialize<WorkspaceTestBatchRunAudit>(
                        File.ReadAllText(filePath),
                        jsonOptions);
                    if (audit is not null)
                    {
                        entries.Add(new WorkspaceTestBatchRunHistoryEntry(audit, filePath));
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or NotSupportedException)
                {
                    // One partial or incompatible record must not hide the remaining history.
                }
            }

            return entries
                .OrderByDescending(static entry => entry.Audit.StartedUtc)
                .ThenByDescending(static entry => entry.Audit.BatchRunId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string NormalizeRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var normalized = new string(runId.Trim()
            .Where(static character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray());
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Batch run ID must contain a letter or number.", nameof(runId));
        }

        return normalized;
    }

    private static void TryRestrictFilePermissions(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
        }
    }
}
