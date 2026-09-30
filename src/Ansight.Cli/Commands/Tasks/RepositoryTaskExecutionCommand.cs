using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host;

namespace Ansight.Cli.Commands.Tasks;

internal static class RepositoryTaskExecutionCommand
{
    public static async Task<int> RunAsync(
        RuntimeCoordinator runtime,
        string appId,
        string repositoryPath,
        string taskId,
        CliArguments arguments,
        CliOutput output,
        CancellationToken cancellationToken)
    {
        ValidateArguments(arguments);
        var requestedSessionId = arguments.GetOption("session-id");
        var requestedDeviceId = arguments.GetOption("device-id");
        var input = ReadTaskInput(arguments);
        var secretValues = arguments.GetOptions("secret")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(alias => alias, alias => CliCommandContext.Current?.ResolveSecret(alias)
                ?? throw new CliUsageException($"Secret '{alias}' is unavailable for this task."), StringComparer.OrdinalIgnoreCase);
        if (CliCommandContext.Current is null)
        {
            throw new CliHostUnavailableException(
                "Repository task execution requires a resident host. Run 'ansight host run', open the app in a simulator, then retry.");
        }

        var sessionId = ResolveSessionId(runtime, appId, requestedSessionId, requestedDeviceId);
        var result = await runtime.RunRepositoryTaskAsync(
            Path.GetFullPath(repositoryPath),
            appId,
            sessionId,
            taskId,
            input,
            cancellationToken,
            secretValues).ConfigureAwait(false);
        output.Write(
            new RepositoryTaskRunOutput("ansight.repository-task-run/v1", result),
            () => RenderTaskResult(result));
        return result.Status switch
        {
            RepositoryTaskRunStatus.Passed => CliExitCodes.Success,
            RepositoryTaskRunStatus.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.Failure
        };
    }

    internal static void ValidateArguments(CliArguments arguments)
    {
        if (arguments.GetOption("session-id") is not null
            && arguments.GetOption("device-id") is not null)
        {
            throw new CliUsageException("Use only one of --session-id or --device-id.");
        }

        _ = ReadTaskInput(arguments);
    }

    private static string ResolveSessionId(
        RuntimeCoordinator runtime,
        string appId,
        string? requestedSessionId,
        string? requestedDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId))
        {
            return requestedSessionId.Trim();
        }

        var sessions = runtime.Sessions.GetSummaries()
            .Where(candidate => string.Equals(candidate.AppId, appId, StringComparison.Ordinal))
            .Where(candidate => runtime.AppTools.IsConnected(candidate.SessionId));
        if (!string.IsNullOrWhiteSpace(requestedDeviceId))
        {
            var normalizedDeviceId = requestedDeviceId.Trim();
            sessions = sessions.Where(candidate => string.Equals(
                ResolveNativeDeviceIdentifier(candidate),
                normalizedDeviceId,
                StringComparison.OrdinalIgnoreCase));
        }

        var session = sessions
            .OrderByDescending(static candidate => candidate.LastUpdatedUtc)
            .FirstOrDefault();
        if (session is not null)
        {
            return session.SessionId;
        }

        throw new CliHostUnavailableException(
            string.IsNullOrWhiteSpace(requestedDeviceId)
                ? $"No connected Ansight session was found for app '{appId}'. Open the app in a simulator and wait for it to connect."
                : $"No connected Ansight session was found for app '{appId}' on device '{requestedDeviceId.Trim()}'. Open the app on that device and wait for it to connect.");
    }

    private static string? ResolveNativeDeviceIdentifier(AppSessionSnapshot snapshot)
    {
        foreach (var stream in snapshot.LogStreams)
        {
            if (stream.Metadata.TryGetValue("deviceUdid", out var deviceUdid)
                && !string.IsNullOrWhiteSpace(deviceUdid))
            {
                return deviceUdid.Trim();
            }

            if (stream.Metadata.TryGetValue("deviceSerial", out var deviceSerial)
                && !string.IsNullOrWhiteSpace(deviceSerial))
            {
                return deviceSerial.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.DeviceProfileJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshot.DeviceProfileJson);
            return document.RootElement.TryGetProperty("device", out var device)
                   && device.ValueKind == JsonValueKind.Object
                   && device.TryGetProperty("nativeDeviceId", out var nativeDeviceId)
                   && nativeDeviceId.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(nativeDeviceId.GetString())
                ? nativeDeviceId.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? ReadTaskInput(CliArguments arguments)
    {
        var inlineJson = arguments.GetOption("input");
        var filePath = arguments.GetOption("input-file");
        if (inlineJson is not null && filePath is not null)
        {
            throw new CliUsageException("Use only one of --input or --input-file.");
        }

        string? source = inlineJson;
        if (filePath is not null)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                throw new CliUsageException($"Task input file '{fullPath}' was not found.");
            }

            source = File.ReadAllText(fullPath);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(
                source,
                documentOptions: new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }) as JsonObject
                   ?? throw new CliUsageException("Repository task input must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"Repository task input is not valid JSON: {exception.Message}");
        }
    }

    private static string RenderTaskResult(RepositoryTaskRunResult result)
    {
        var summary = $"{result.Status}: task '{result.TaskId}' for '{result.AppId}' "
                      + $"on session '{result.SessionId}' in {result.DurationMilliseconds}ms. {result.Message}";
        if (result.Assertions.Count == 0)
        {
            return summary;
        }

        return summary
               + Environment.NewLine
               + string.Join(
                   Environment.NewLine,
                   result.Assertions.Select(assertion =>
                       $"{(assertion.Passed ? "PASS" : "FAIL")}\t{assertion.AssertionId}\t{assertion.Message}"));
    }
}
