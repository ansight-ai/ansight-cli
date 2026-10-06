using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ansight.Host.Workspaces;

namespace Ansight.Host.SimulatorAgent.Auditing;

internal sealed class AuditStore : IAuditStore
{
    private const string AuditDirectoryName = "simulator-agent-runs";
    private readonly string auditDirectoryPath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonUtil.Pretty)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AuditStore(IApplicationPaths applicationPaths)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        auditDirectoryPath = Path.Combine(applicationPaths.ApplicationDataPath, AuditDirectoryName);
    }

    public AuditSaveResult Save(SimulatorAgentRunAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        string? traceDirectoryPath = null;
        try
        {
            PrivateStorageDirectory.Ensure(auditDirectoryPath);
            var timestamp = audit.StartedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
            var filePath = Path.Combine(auditDirectoryPath, $"{timestamp}-{audit.RunId}.json");
            var temporaryPath = filePath + ".tmp";
            var persistedAudit = PersistOcrTraceEvidence(audit, filePath, out traceDirectoryPath);
            // Billing recovery metadata is kept outside the recorded/exported trace.
            var metering = new JsonArray();
            foreach (var usage in audit.CreateModelPassUsages())
            {
                var response = JsonSerializer.SerializeToNode(usage, jsonOptions)!.AsObject();
                response["model"] = usage.Model;
                metering.Add(response);
            }
            var meteringPath = filePath + ".metering";
            File.WriteAllText(meteringPath + ".tmp", metering.ToJsonString(jsonOptions));
            File.Move(meteringPath + ".tmp", meteringPath, overwrite: true);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persistedAudit, jsonOptions));
            File.Move(temporaryPath, filePath, overwrite: true);
            RestrictFile(meteringPath);
            RestrictFile(filePath);
            BoundedAuditHistory.Prune(auditDirectoryPath, filePath, includeTraceDirectories: true);
            return new AuditSaveResult(filePath, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteTraceDirectory(traceDirectoryPath);
            return new AuditSaveResult(null, exception.Message);
        }
    }

    private static SimulatorAgentRunAudit PersistOcrTraceEvidence(
        SimulatorAgentRunAudit audit,
        string auditFilePath,
        out string? traceDirectoryPath)
    {
        traceDirectoryPath = null;
        if (audit.TraceEnabled != true
            || !audit.ToolCalls.Any(static call => call.OcrEvidence is not null))
        {
            return audit;
        }

        var traceDirectoryName = $"{Path.GetFileNameWithoutExtension(auditFilePath)}.trace";
        traceDirectoryPath = Path.Combine(
            Path.GetDirectoryName(auditFilePath)!,
            traceDirectoryName);
        PrivateStorageDirectory.Ensure(traceDirectoryPath);

        var toolCalls = new List<SimulatorAgentToolCallAudit>(audit.ToolCalls.Count);
        foreach (var call in audit.ToolCalls)
        {
            var evidence = call.OcrEvidence;
            if (evidence is null)
            {
                toolCalls.Add(call);
                continue;
            }

            var fileStem = $"ocr-{call.Sequence:D4}";
            string? screenshotRelativePath = null;
            if (!string.IsNullOrWhiteSpace(evidence.SourceScreenshotPath)
                && File.Exists(evidence.SourceScreenshotPath))
            {
                var screenshotExtension = ResolveScreenshotExtension(
                    evidence.SourceScreenshotPath,
                    evidence.ScreenshotFormat);
                var screenshotFileName = fileStem + screenshotExtension;
                var screenshotDestinationPath = Path.Combine(traceDirectoryPath, screenshotFileName);
                File.Copy(evidence.SourceScreenshotPath, screenshotDestinationPath, overwrite: true);
                RestrictFile(screenshotDestinationPath);
                screenshotRelativePath = $"{traceDirectoryName}/{screenshotFileName}";
            }

            var resultsFileName = fileStem + ".json";
            File.WriteAllText(
                Path.Combine(traceDirectoryPath, resultsFileName),
                evidence.Results.Content);
            RestrictFile(Path.Combine(traceDirectoryPath, resultsFileName));
            var persistedEvidence = evidence with
            {
                ScreenshotPath = screenshotRelativePath,
                ResultsPath = $"{traceDirectoryName}/{resultsFileName}"
            };
            toolCalls.Add(call with { OcrEvidence = persistedEvidence });
        }

        return audit with { ToolCalls = toolCalls };
    }

    private static string ResolveScreenshotExtension(string sourcePath, string? format)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            return extension;
        }

        return string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase)
               || string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : ".png";
    }

    private static void TryDeleteTraceDirectory(string? traceDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(traceDirectoryPath))
        {
            return;
        }

        try
        {
            Directory.Delete(traceDirectoryPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next retention pass can remove an orphaned trace directory.
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public IReadOnlyList<SimulatorAgentRunHistoryEntry> List()
    {
        try
        {
            if (!Directory.Exists(auditDirectoryPath))
            {
                return Array.Empty<SimulatorAgentRunHistoryEntry>();
            }

            var entries = new List<SimulatorAgentRunHistoryEntry>();
            foreach (var filePath in Directory.EnumerateFiles(auditDirectoryPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var audit = JsonSerializer.Deserialize<SimulatorAgentRunAudit>(
                        File.ReadAllText(filePath),
                        jsonOptions);
                    if (audit is not null)
                    {
                        var meteringPath = filePath + ".metering";
                        if (File.Exists(meteringPath))
                        {
                            try
                            {
                                audit = audit with
                                {
                                    MeteringResponses = JsonSerializer.Deserialize<SimulatorAgentModelPassUsage[]>(
                                        File.ReadAllText(meteringPath), jsonOptions)
                                };
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                            {
                                // Billing metadata must not hide a readable trace.
                            }
                        }
                        entries.Add(new SimulatorAgentRunHistoryEntry(audit, filePath));
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException
                                                   or NotSupportedException)
                {
                    // A partial or incompatible audit must not hide the rest of the run history.
                }
            }

            return entries
                .OrderByDescending(static entry => entry.Audit.StartedUtc)
                .ThenByDescending(static entry => entry.Audit.RunId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<SimulatorAgentRunHistoryEntry>();
        }
    }
}
