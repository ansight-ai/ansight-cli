using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.WebSocketSessions;

[Export]
internal sealed class CrashReportReceiver
{
    public const string HandoffAction = "crash.handoff";
    internal const int MaximumReportBytes = 8 * 1024 * 1024;
    private const int MaximumTraceBytes = 4 * 1024 * 1024;
    private readonly Lock gate = new();
    private readonly IRuntimeState runtimeState;
    private readonly SessionCaptureStore captureStore;
    private readonly string temporaryDirectory;

    [ImportingConstructor]
    public CrashReportReceiver(IRuntimeState runtimeState, SessionCaptureStore captureStore, IApplicationPaths paths)
    {
        this.runtimeState = runtimeState;
        this.captureStore = captureStore;
        temporaryDirectory = paths.ApplicationTempPath;
    }

    public OperationResult Receive(string deliverySessionId, JsonObject? payload)
    {
        try
        {
            lock (gate)
            {
                return ReceiveCore(deliverySessionId, payload);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return OperationResult.Failure($"Could not retain crash report: {exception.Message}");
        }
    }

    private OperationResult ReceiveCore(string deliverySessionId, JsonObject? payload)
    {
        if (payload?["report"] is not JsonObject report
            || ReadString(report, "schema") != "ansight.crash.v1"
            || !runtimeState.TryGetSessionSnapshot(deliverySessionId, out var delivery)
            || delivery is null)
        {
            return OperationResult.Failure("A crash handoff requires a current session and an ansight.crash.v1 report.");
        }

        var reportId = ReadString(payload, "reportId");
        var processId = ReadString(payload, "targetProcessSessionId");
        if (string.IsNullOrWhiteSpace(reportId) || reportId.Length > 128
            || string.IsNullOrWhiteSpace(processId) || processId.Length > 128
            || reportId != ReadString(report, "reportId")
            || processId != ReadString(report, "previousProcessSessionId")
            || string.IsNullOrWhiteSpace(delivery.ProcessSessionId)
            || ReadString(payload, "deliveryProcessSessionId") != delivery.ProcessSessionId
            || (ReadString(report, "appId") is { } appId && appId != delivery.AppId))
        {
            return OperationResult.Failure("Crash report identity does not match the handoff or delivering app.");
        }

        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report);
        if (reportBytes.Length > MaximumReportBytes)
        {
            return OperationResult.Failure("Crash report exceeds the 8 MiB handoff limit.");
        }

        if (!DateTimeOffset.TryParse(ReadString(report, "occurredAtUtc"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var occurredAt))
        {
            return OperationResult.Failure("Crash report occurrence time is invalid.");
        }

        var targetSessionId = ReadString(payload, "targetSessionId");
        AppSessionSnapshot? target = null;
        if (!string.IsNullOrWhiteSpace(targetSessionId))
        {
            runtimeState.TryGetSessionSnapshot(targetSessionId, out target);
        }

        target ??= runtimeState.GetSessionSummaries().FirstOrDefault(session =>
            session.AppId == delivery.AppId && session.ProcessSessionId == processId);
        if (target is not null && (target.AppId != delivery.AppId || target.ProcessSessionId != processId
            || target.ConfigId != delivery.ConfigId))
        {
            return OperationResult.Failure("Crash report target does not belong to this app registration and process.");
        }

        // A crash can precede the first host connection, or its original capture may have expired.
        // Retain it on the delivering session with the original process identity in report.json.
        target ??= delivery;
        var snapshotId = "native-crash-" + Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { processId, reportId })));
        var directory = Path.Combine(temporaryDirectory, "native-crashes", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "report.json"), reportBytes);
            WriteTrace(report, "traceBase64", directory, "native.trace");
            WriteTrace(report, "metricKitPayloadBase64", directory, "metrickit.json");
            var entries = Directory.GetFiles(directory).Order(StringComparer.Ordinal).Select(path =>
            {
                var file = new FileInfo(path);
                return new SessionArtifactEntry
                {
                    Name = file.Name,
                    RootAlias = "native-crashes",
                    RelativePath = file.Name,
                    SnapshotRelativePath = file.Name,
                    ArchiveRelativePath = file.Name,
                    Kind = "file",
                    SizeBytes = file.Length,
                    FileExtension = file.Extension,
                    MimeType = file.Extension == ".json" ? "application/json" : "application/octet-stream"
                };
            }).ToArray();
            var artifact = new SessionArtifactSnapshot
            {
                SnapshotId = snapshotId,
                CapturedAtUtc = occurredAt.ToUniversalTime(),
                Source = "sdk.native.crash-report",
                RootAlias = "native-crashes",
                RootPath = "native-crashes",
                RelativePath = "report.json",
                Name = "Recovered native crash",
                Kind = "crash-report",
                ArtifactDirectoryName = snapshotId,
                FileCount = entries.Length,
                ByteCount = entries.Sum(entry => entry.SizeBytes),
                Entries = entries
            };
            var result = runtimeState.AddSessionArtifactSnapshot(target.SessionId, artifact, directory);
            if (!result.IsSuccess)
            {
                return result;
            }

            if (!runtimeState.TryGetSessionSnapshot(target.SessionId, out var updated) || updated is null)
            {
                return OperationResult.Failure("Crash report session is no longer available.");
            }

            // The normal runtime save is queued. Acknowledge only after the artifact index and
            // session summary have also been written, so a host restart cannot orphan the report.
            captureStore.Save(updated);
            return OperationResult.Success($"Crash report retained in session '{target.SessionId}'.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Temporary cleanup must not change a durable acknowledgement.
            }
        }
    }

    private static string? ReadString(JsonObject value, string key)
        => value[key]?.GetValue<string>();

    private static void WriteTrace(JsonObject report, string key, string directory, string name)
    {
        if (ReadString(report, key) is not { } encoded) return;
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaximumTraceBytes) throw new InvalidDataException("Crash trace exceeds 4 MiB.");
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
    }
}
