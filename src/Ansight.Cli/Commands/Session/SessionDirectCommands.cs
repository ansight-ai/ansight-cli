using System.Globalization;
using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal static class SessionDirectCommands
{
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> ExtractAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session extract <session-id> (--annotation <id> | --start <timestamp> --end <timestamp>) [--name <name>]");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var snapshot = await runtime.Sessions.LoadSnapshotAsync(sessionId, null, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return WriteSessionNotFound(sessionId, output);
        }

        SessionExtractionResult result;
        string source;
        if (arguments.GetOption("annotation") is { } annotationId)
        {
            if (arguments.GetOption("start") is not null || arguments.GetOption("end") is not null)
            {
                throw new CliUsageException("Use either --annotation or --start/--end, not both.");
            }

            source = $"annotation:{annotationId}";
            result = runtime.SessionEditing.ExtractAnnotationBounds(
                snapshot.SessionId,
                annotationId,
                arguments.GetOption("name"));
        }
        else
        {
            var startUtc = ParseTimestamp(arguments.RequireOption("start"), "start");
            var endUtc = ParseTimestamp(arguments.RequireOption("end"), "end");
            source = $"{startUtc:O}/{endUtc:O}";
            result = runtime.SessionEditing.ExtractTimelineRange(
                snapshot.SessionId,
                startUtc,
                endUtc,
                arguments.GetOption("name"));
        }

        output.Write(
            new SessionExtractionOutput(
                "ansight.session-extraction/v1",
                snapshot.SessionId,
                source,
                result),
            () => result.Message
                  + (result.ExtractedSession is null
                      ? string.Empty
                      : $"{Environment.NewLine}Session: {result.ExtractedSession.SessionId}"));
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int Trim(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(
            3,
            "ansight session trim <session-id> --start <timestamp> --end <timestamp> --mode <cut|keep>");
        var mode = arguments.GetOption("mode")?.Trim().ToLowerInvariant() switch
        {
            "cut" or "remove" or "cut-selection" => SessionTimelineTrimMode.CutSelection,
            "keep" or "keep-only" or "keep-selection" => SessionTimelineTrimMode.KeepSelectionOnly,
            _ => throw new CliUsageException("--mode must be cut or keep.")
        };
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var startUtc = ParseTimestamp(arguments.RequireOption("start"), "start");
        var endUtc = ParseTimestamp(arguments.RequireOption("end"), "end");
        var result = runtime.SessionEditing.TrimTimeline(sessionId, startUtc, endUtc, mode);
        return WriteOperation("trim", sessionId, result, output);
    }

    public static int Normalize(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        arguments.EnsurePositionalCount(3, "ansight session normalize <session-id>");
        var sessionId = arguments.RequirePositional(2, "session identifier");
        var result = runtime.SessionEditing.Normalize(sessionId);
        output.Write(
            new SessionNormalizationOutput(
                "ansight.session-normalization/v1",
                sessionId,
                result),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static async Task<int> AnnotationAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(2, "annotation action").ToLowerInvariant();
        var sessionId = arguments.RequirePositional(3, "session identifier");
        if (action == "status")
        {
            arguments.EnsurePositionalCount(
                5,
                "ansight session annotation status <session-id> <annotation-id> (--status <text> | --clear-status)");
            var status = arguments.GetOption("status");
            var clearStatus = arguments.HasFlag("clear-status");
            if (clearStatus == (status is not null))
            {
                throw new CliUsageException("Pass either --status <text> or --clear-status.");
            }

            var annotationId = arguments.RequirePositional(4, "annotation identifier");
            var statusResult = runtime.SessionEditing.SetAnnotationStatus(sessionId, annotationId, clearStatus ? null : status);
            output.Write(
                new SessionAnnotationStatusOutput(
                    "ansight.session-annotation-status/v1",
                    sessionId,
                    annotationId,
                    clearStatus ? null : status,
                    statusResult),
                () => statusResult.Message);
            return statusResult.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
        }
        if (action is "delete" or "remove")
        {
            arguments.EnsurePositionalCount(
                5,
                "ansight session annotation delete <session-id> <annotation-id>");
            return WriteOperation(
                "annotation-delete",
                sessionId,
                runtime.SessionEditing.DeleteAnnotation(
                    sessionId,
                    arguments.RequirePositional(4, "annotation identifier")),
                output);
        }

        if (action is not ("add" or "create" or "update" or "upsert"))
        {
            throw new CliUsageException(
                $"Unknown annotation action '{action}'. Expected add, update, upsert, status, or delete.");
        }

        arguments.EnsurePositionalCount(
            4,
            "ansight session annotation upsert <session-id> (--file <annotation.json> | --label <label> --start <timestamp>)");
        SessionAnnotation annotation;
        if (arguments.GetOption("file") is { } filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Annotation file '{fullPath}' was not found.");
            }

            try
            {
                annotation = JsonSerializer.Deserialize<SessionAnnotation>(
                                 await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false),
                                 jsonOptions)
                             ?? throw new CliUsageException("The annotation JSON was empty.");
            }
            catch (JsonException exception)
            {
                throw new CliUsageException($"Unable to parse annotation JSON: {exception.Message}");
            }
        }
        else
        {
            annotation = new SessionAnnotation
            {
                AnnotationId = arguments.GetOption("annotation-id") ?? Guid.NewGuid().ToString("N"),
                StartUtc = ParseTimestamp(arguments.RequireOption("start"), "start"),
                EndUtc = ParseOptionalTimestamp(arguments.GetOption("end"), "end"),
                Label = arguments.RequireOption("label"),
                Notes = arguments.GetOption("notes"),
                Status = arguments.GetOption("status"),
                Source = arguments.GetOption("source") ?? "cli"
            };
        }

        var result = runtime.SessionEditing.UpsertAnnotation(sessionId, annotation);
        output.Write(
            new SessionAnnotationOperationOutput(
                "ansight.session-annotation-operation/v1",
                sessionId,
                result,
                annotation),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static int Analysis(RuntimeCoordinator runtime, CliArguments arguments, CliOutput output)
    {
        var action = arguments.RequirePositional(2, "analysis action").ToLowerInvariant();
        if (action is not ("delete" or "remove"))
        {
            throw new CliUsageException($"Unknown analysis action '{action}'. Expected delete.");
        }

        arguments.EnsurePositionalCount(
            5,
            "ansight session analysis delete <session-id> <analysis-id>");
        var sessionId = arguments.RequirePositional(3, "session identifier");
        return WriteOperation(
            "analysis-delete",
            sessionId,
            runtime.SessionEditing.DeleteAnalysis(
                sessionId,
                arguments.RequirePositional(4, "analysis identifier")),
            output);
    }

    public static async Task<int> ScreenshotAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(2, "screenshot action").ToLowerInvariant();
        if (action is not ("get" or "read" or "export" or "download"))
        {
            throw new CliUsageException(
                $"Unknown screenshot action '{action}'. Expected get or export.");
        }

        arguments.EnsurePositionalCount(
            4,
            "ansight session screenshot export <session-id> [--frame-id <id>|--timestamp <utc>] --output <path>");
        var outputPath = Path.GetFullPath(arguments.RequireOption("output"));
        EnsureWritableDestination(outputPath, arguments);
        var sessionId = arguments.RequirePositional(3, "session identifier");
        var result = await runtime.SessionEvidence.ExportSessionScreenshotAsync(
            sessionId,
            arguments.GetOption("frame-id"),
            ParseOptionalTimestamp(arguments.GetOption("timestamp"), "timestamp"),
            outputPath,
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new SessionScreenshotExportOutput(
                "ansight.session-screenshot-export/v1",
                sessionId,
                result),
            () => result.IsSuccess ? $"Screenshot: {result.FilePath}" : result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    public static async Task<int> ArtifactAsync(
        RuntimeCoordinator runtime,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        var action = arguments.RequirePositional(2, "artifact action").ToLowerInvariant();
        if (action is not ("get" or "read" or "export" or "download"))
        {
            throw new CliUsageException(
                $"Unknown artifact action '{action}'. Expected read or export.");
        }

        arguments.EnsurePositionalCount(
            4,
            "ansight session artifact export <session-id> --path <artifact-path> --output <path> [--snapshot-id <id>]");
        var outputPath = Path.GetFullPath(arguments.RequireOption("output"));
        EnsureWritableDestination(outputPath, arguments);
        var sessionId = arguments.RequirePositional(3, "session identifier");
        var result = await runtime.SessionEvidence.ExportSessionArtifactFileAsync(
            sessionId,
            arguments.GetOption("snapshot-id"),
            arguments.RequireOption("path"),
            outputPath,
            cancellationToken).ConfigureAwait(false);
        output.Write(
            new SessionArtifactExportOutput(
                "ansight.session-artifact-export/v1",
                sessionId,
                result),
            () => result.IsSuccess ? $"Artifact: {result.FilePath}" : result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }

    private static void EnsureWritableDestination(string outputPath, CliArguments arguments)
    {
        if (File.Exists(outputPath) && !arguments.HasFlag("force") && !arguments.HasFlag("overwrite"))
        {
            throw new CliUsageException(
                $"Output file '{outputPath}' already exists. Pass --force to replace it.");
        }
    }

    private static DateTimeOffset ParseTimestamp(string value, string optionName)
        => ParseOptionalTimestamp(value, optionName)
           ?? throw new CliUsageException($"--{optionName} requires a timestamp.");

    private static DateTimeOffset? ParseOptionalTimestamp(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return timestamp.ToUniversalTime();
        }

        throw new CliUsageException($"--{optionName} must be an ISO-8601 timestamp.");
    }

    private static int WriteSessionNotFound(string sessionId, CliOutput output)
    {
        output.WriteError("session_not_found", $"Session '{sessionId}' was not found.", CliExitCodes.Failure);
        return CliExitCodes.Failure;
    }

    private static int WriteOperation(
        string operation,
        string sessionId,
        OperationResult result,
        CliOutput output)
    {
        output.Write(
            new SessionDirectOperationOutput(
                "ansight.session-operation/v1",
                operation,
                sessionId,
                result),
            () => result.Message);
        return result.IsSuccess ? CliExitCodes.Success : CliExitCodes.Failure;
    }
}

internal sealed record SessionExtractionOutput(
    string Schema,
    string SourceSessionId,
    string Source,
    SessionExtractionResult Result);

internal sealed record SessionAnnotationOperationOutput(
    string Schema,
    string SessionId,
    OperationResult Result,
    SessionAnnotation Annotation);

internal sealed record SessionAnnotationStatusOutput(
    string Schema,
    string SessionId,
    string AnnotationId,
    string? Status,
    OperationResult Result);

internal sealed record SessionScreenshotExportOutput(
    string Schema,
    string SessionId,
    SessionScreenshotExportResult Result);

internal sealed record SessionArtifactExportOutput(
    string Schema,
    string SessionId,
    SessionArtifactExportResult Result);

internal sealed record SessionDirectOperationOutput(
    string Schema,
    string Operation,
    string SessionId,
    OperationResult Result);

internal sealed record SessionNormalizationOutput(
    string SchemaVersion,
    string SessionId,
    SessionNormalizationResult Result);
