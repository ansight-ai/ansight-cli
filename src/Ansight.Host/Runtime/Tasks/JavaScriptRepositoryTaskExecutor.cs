using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ansight.Host.Audio;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.RepositoryContracts;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class JavaScriptRepositoryTaskExecutor
{
    private static readonly TimeSpan moduleLoadTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumStandardErrorCharacters = 65_536;
    private const int MaximumToolResultCharacters = 524_288;
    private const int MaximumTaskResultCharacters = 262_144;
    private static readonly string bootstrapSource = RepositoryModuleContractArtifacts.GetTaskRuntimeConstants()
        + Environment.NewLine + EmbeddedTextResource
        .Read("Runtime/Tasks/Resources/repository-task-bootstrap.mjs")
        .TrimEnd();

    private readonly Func<string, CancellationToken, Task<JsonObject>>? capabilityResolver;
    private readonly string executablePath;
    private readonly RepositoryTaskToolExecutor toolExecutor;
    private readonly string? unavailableMessage;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> hostApiSuites;
    private readonly IReadOnlySet<string> standardHostToolNames;
    private readonly RepositoryTaskApiExecutor? taskApiExecutor;
    private readonly IReadOnlySet<string> standardTaskMethodNames;

    public JavaScriptRepositoryTaskExecutor(
        string executablePath,
        RepositoryTaskToolExecutor toolExecutor,
        string? unavailableMessage = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? hostApiSuites = null,
        RepositoryTaskApiExecutor? taskApiExecutor = null,
        Func<string, CancellationToken, Task<JsonObject>>? capabilityResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.capabilityResolver = capabilityResolver;
        this.executablePath = executablePath.Trim();
        this.toolExecutor = toolExecutor ?? throw new ArgumentNullException(nameof(toolExecutor));
        this.unavailableMessage = string.IsNullOrWhiteSpace(unavailableMessage)
            ? null
            : unavailableMessage.Trim();
        this.hostApiSuites = hostApiSuites is null
            ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            : hostApiSuites.ToDictionary(
                suite => suite.Key,
                suite => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    suite.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        standardHostToolNames = this.hostApiSuites.Values
            .SelectMany(static suite => suite.Values)
            .ToHashSet(StringComparer.Ordinal);
        this.taskApiExecutor = taskApiExecutor;
        standardTaskMethodNames = RepositoryJavaScriptApiMethods.TaskCompositionSuites.Values
            .SelectMany(static suite => suite.Values)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<RepositoryTaskRunResult> ExecuteAsync(
        RepositoryTaskExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { SourceCapture = request.CaptureTrace ? new RepositoryTaskSourceCapture() : null };
        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var toolCalls = new List<RepositoryTaskToolCall>();
        var recordedAssertions = new List<RepositoryTaskAssertion>();
        if (unavailableMessage is not null)
        {
            return Finish(
                request,
                RepositoryTaskRunStatus.Rejected,
                startedAtUtc,
                stopwatch,
                unavailableMessage,
                null,
                recordedAssertions,
                toolCalls,
                string.Empty);
        }

        Process? process = null;
        Task<string>? standardErrorTask = null;
        try
        {
            if (capabilityResolver is not null)
            {
                using var preflight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                preflight.CancelAfter(TimeSpan.FromSeconds(15));
                var capabilities = await capabilityResolver(request.SessionId, preflight.Token).ConfigureAwait(false);
                request = request with { Capabilities = capabilities };
                if (ExecutionCapabilities.Missing(capabilities, request.Task.Requires, request.Task.SchemaVersion < 2) is { } missing)
                    return Finish(request, RepositoryTaskRunStatus.Rejected, startedAtUtc, stopwatch,
                        missing["message"]!.GetValue<string>(), missing, [], toolCalls, string.Empty);
            }
            else if (request.Task.SchemaVersion >= 2)
            {
                return Finish(request, RepositoryTaskRunStatus.Rejected, startedAtUtc, stopwatch,
                    "The task host cannot resolve execution capabilities.", null, [], toolCalls, string.Empty);
            }
            process = Process.Start(CreateStartInfo(request.Task, request.CaptureTrace));
            if (process is null)
            {
                return Finish(
                    request,
                    RepositoryTaskRunStatus.Error,
                    startedAtUtc,
                    stopwatch,
                    "The repository task Node.js runtime did not start.",
                    null,
                    recordedAssertions,
                    toolCalls,
                    string.Empty);
            }

            standardErrorTask = ReadBoundedTextAsync(
                process.StandardError,
                MaximumStandardErrorCharacters,
                CancellationToken.None);
            string? handshake;
            using (var moduleLoadTimeoutCts = new CancellationTokenSource(moduleLoadTimeout))
            using (var moduleLoadCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       moduleLoadTimeoutCts.Token))
            {
                try
                {
                    do
                    {
                        handshake = await process.StandardOutput.ReadLineAsync(moduleLoadCts.Token).ConfigureAwait(false);
                    }
                    while (TryParseMessage(handshake, out var sourceMessage)
                           && sourceMessage is not null
                           && request.SourceCapture?.TryRead(sourceMessage) == true);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    await ObserveExitAsync(process).ConfigureAwait(false);
                    var standardError = await standardErrorTask.ConfigureAwait(false);
                    return Finish(
                        request,
                        cancellationToken.IsCancellationRequested
                            ? RepositoryTaskRunStatus.Cancelled
                            : RepositoryTaskRunStatus.TimedOut,
                        startedAtUtc,
                        stopwatch,
                        cancellationToken.IsCancellationRequested
                            ? "Repository task execution was cancelled while loading its module."
                            : $"Task module loading exceeded the host's {moduleLoadTimeout.TotalSeconds:0}-second limit.",
                        null,
                        recordedAssertions,
                        toolCalls,
                        standardError);
                }
            }

            if (!TryParseMessage(handshake, out var handshakeMessage)
                || !string.Equals(handshakeMessage?["type"]?.GetValue<string>(), "ready", StringComparison.Ordinal))
            {
                TryKill(process);
                await ObserveExitAsync(process).ConfigureAwait(false);
                var standardError = await standardErrorTask.ConfigureAwait(false);
                return Finish(
                    request,
                    RepositoryTaskRunStatus.Error,
                    startedAtUtc,
                    stopwatch,
                    "The task module did not complete the Ansight startup handshake.",
                    null,
                    recordedAssertions,
                    toolCalls,
                    standardError);
            }

            await process.StandardInput.WriteLineAsync(CreateInvocation(request).ToJsonString(JsonUtil.Compact))
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            using var taskTimeoutCts = new CancellationTokenSource(request.Task.Timeout);
            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                taskTimeoutCts.Token);
            try
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(taskCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        var standardError = await standardErrorTask.ConfigureAwait(false);
                        return Finish(
                            request,
                            RepositoryTaskRunStatus.Error,
                            startedAtUtc,
                            stopwatch,
                            "The task process exited without returning a result.",
                            null,
                            recordedAssertions,
                            toolCalls,
                            standardError);
                    }

                    if (!TryParseMessage(line, out var message) || message is null)
                    {
                        TryKill(process);
                        await ObserveExitAsync(process).ConfigureAwait(false);
                        return Finish(
                            request,
                            RepositoryTaskRunStatus.Error,
                            startedAtUtc,
                            stopwatch,
                            "The task process returned malformed protocol JSON.",
                            null,
                            recordedAssertions,
                            toolCalls,
                            await standardErrorTask.ConfigureAwait(false));
                    }

                    if (request.SourceCapture?.TryRead(message) == true) continue;
                    var messageType = message["type"]?.GetValue<string>();
                    if (string.Equals(messageType, "assertion", StringComparison.Ordinal)
                        && message["assertion"] is JsonObject assertion)
                    {
                        recordedAssertions.AddRange(ParseAssertions(new JsonArray(assertion.DeepClone())));
                        continue;
                    }
                    if (string.Equals(messageType, "call", StringComparison.Ordinal))
                    {
                        var callFailure = await HandleToolCallAsync(
                            process,
                            request,
                            message,
                            toolCalls,
                            taskCts.Token).ConfigureAwait(false);
                        if (callFailure is not null)
                        {
                            TryKill(process);
                            await ObserveExitAsync(process).ConfigureAwait(false);
                            return Finish(
                                request,
                                callFailure.Value.Status,
                                startedAtUtc,
                                stopwatch,
                                callFailure.Value.Message,
                                null,
                                recordedAssertions,
                                toolCalls,
                                await standardErrorTask.ConfigureAwait(false));
                        }

                        continue;
                    }

                    if (!string.Equals(messageType, "result", StringComparison.Ordinal))
                    {
                        TryKill(process);
                        await ObserveExitAsync(process).ConfigureAwait(false);
                        return Finish(
                            request,
                            RepositoryTaskRunStatus.Error,
                            startedAtUtc,
                            stopwatch,
                            $"The task process returned unsupported message type '{messageType ?? "(missing)"}'.",
                            null,
                            recordedAssertions,
                            toolCalls,
                            await standardErrorTask.ConfigureAwait(false));
                    }

                    process.StandardInput.Close();
                    await process.WaitForExitAsync(taskCts.Token).ConfigureAwait(false);
                    // The streamed values capture what was compared at the time of the check,
                    // even if a task later mutates an object used by an expectation.
                    IReadOnlyList<RepositoryTaskAssertion> assertions = recordedAssertions.Count > 0
                        ? recordedAssertions : ParseAssertions(message["assertions"] as JsonArray);
                    var status = ParseStatus(message["status"]?.GetValue<string>());
                    var resultMessage = message["message"]?.GetValue<string>()
                                        ?? "Repository task completed.";
                    if ((status is RepositoryTaskRunStatus.Passed or RepositoryTaskRunStatus.Inconclusive)
                        && assertions.Any(static assertion => !assertion.Passed))
                    {
                        status = RepositoryTaskRunStatus.Failed;
                        resultMessage = $"Task failed {assertions.Count(static assertion => !assertion.Passed)} "
                                        + $"of {assertions.Count} assertion(s).";
                    }
                    else if (status == RepositoryTaskRunStatus.Passed && assertions.Count == 0)
                    {
                        status = RepositoryTaskRunStatus.Inconclusive;
                        resultMessage = "Task completed without assertions.";
                    }

                    var output = message["output"]?.DeepClone();
                    if (output?.ToJsonString().Length > MaximumTaskResultCharacters)
                    {
                        output = new JsonObject
                        {
                            ["truncated"] = true,
                            ["message"] = "Task output exceeded the persisted result limit."
                        };
                    }

                    return Finish(
                        request,
                        status,
                        startedAtUtc,
                        stopwatch,
                        resultMessage,
                        output,
                        assertions,
                        toolCalls,
                        await standardErrorTask.ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await ObserveExitAsync(process).ConfigureAwait(false);
                return Finish(
                    request,
                    cancellationToken.IsCancellationRequested
                        ? RepositoryTaskRunStatus.Cancelled
                        : RepositoryTaskRunStatus.TimedOut,
                    startedAtUtc,
                    stopwatch,
                    cancellationToken.IsCancellationRequested
                        ? "Repository task execution was cancelled."
                        : $"Repository task exceeded its {request.Task.Timeout.TotalSeconds:0}-second timeout.",
                    null,
                    recordedAssertions,
                    toolCalls,
                    await standardErrorTask.ConfigureAwait(false));
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidOperationException
                                           or JsonException
                                           or System.ComponentModel.Win32Exception)
        {
            if (process is not null)
            {
                TryKill(process);
                await ObserveExitAsync(process).ConfigureAwait(false);
            }

            var standardError = standardErrorTask is null
                ? string.Empty
                : await standardErrorTask.ConfigureAwait(false);
            return Finish(
                request,
                RepositoryTaskRunStatus.Error,
                startedAtUtc,
                stopwatch,
                exception.Message,
                null,
                recordedAssertions,
                toolCalls,
                standardError);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<RepositoryTaskCallFailure?> HandleToolCallAsync(
        Process process,
        RepositoryTaskExecutionRequest request,
        JsonObject message,
        ICollection<RepositoryTaskToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var callId = message["callId"]?.GetValue<string>();
        var callKind = message["kind"]?.GetValue<string>();
        var requestedName = message["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(callId)
            || string.IsNullOrWhiteSpace(callKind)
            || string.IsNullOrWhiteSpace(requestedName))
        {
            return new RepositoryTaskCallFailure(
                RepositoryTaskRunStatus.Error,
                "The task process returned an incomplete tool call.");
        }

        if (toolCalls.Count >= request.Task.MaximumActions)
        {
            return new RepositoryTaskCallFailure(
                RepositoryTaskRunStatus.Rejected,
                $"Task '{request.Task.TaskId}' exceeded its {request.Task.MaximumActions}-action limit.");
        }

        string toolName;
        var auditName = requestedName;
        var isTaskMethod = false;
        JsonObject arguments;
        if (string.Equals(callKind, "hostMethod", StringComparison.Ordinal))
        {
            if (!standardHostToolNames.Contains(requestedName))
            {
                return new RepositoryTaskCallFailure(
                    RepositoryTaskRunStatus.Rejected,
                    $"Host tool '{requestedName}' is not available through the Ansight task API.");
            }

            toolName = requestedName;
            arguments = message["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
            ApplySessionTarget(arguments, request.SessionId);
        }
        else if (string.Equals(callKind, "hostTool", StringComparison.Ordinal))
        {
            if (!request.Task.DeclaredHostTools.ContainsKey(requestedName))
            {
                return new RepositoryTaskCallFailure(
                    RepositoryTaskRunStatus.Rejected,
                    $"Task '{request.Task.TaskId}' did not declare host tool '{requestedName}'.");
            }

            toolName = requestedName;
            arguments = message["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
            ApplySessionTarget(arguments, request.SessionId);
        }
        else if (string.Equals(callKind, "appMethod", StringComparison.Ordinal)
                 || string.Equals(callKind, "appTool", StringComparison.Ordinal))
        {
            if (string.Equals(callKind, "appMethod", StringComparison.Ordinal)
                && !RepositoryJavaScriptApiMethods.IsStandardAppToolId(requestedName))
            {
                return new RepositoryTaskCallFailure(
                    RepositoryTaskRunStatus.Rejected,
                    $"App tool '{requestedName}' is not available through the standard task API.");
            }

            toolName = "ansight_call_app_tool";
            var appToolArguments = message["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
            RemoveTargetOverrides(appToolArguments);
            arguments = new JsonObject
            {
                ["toolId"] = requestedName,
                ["arguments"] = appToolArguments
            };
            ApplySessionTarget(arguments, request.SessionId);
        }
        else if (string.Equals(callKind, "taskMethod", StringComparison.Ordinal))
        {
            if (!standardTaskMethodNames.Contains(requestedName))
            {
                return new RepositoryTaskCallFailure(
                    RepositoryTaskRunStatus.Rejected,
                    $"Task composition method '{requestedName}' is not available through the Ansight task API.");
            }
            if (taskApiExecutor is null)
            {
                return new RepositoryTaskCallFailure(
                    RepositoryTaskRunStatus.Rejected,
                    "Task composition is not configured.");
            }

            toolName = requestedName;
            auditName = $"ansight.tasks.{requestedName}";
            isTaskMethod = true;
            arguments = message["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
        }
        else
        {
            return new RepositoryTaskCallFailure(
                RepositoryTaskRunStatus.Error,
                $"The task process requested unsupported call kind '{callKind}'.");
        }

        if (string.Equals(toolName, "ansight_create_annotation", StringComparison.Ordinal)
            && !arguments.ContainsKey("source"))
        {
            arguments["source"] = $"task:{request.Task.TaskId}";
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var callCorrelationId = $"{request.CorrelationId}:{toolCalls.Count + 1}";
        var traceAppToolId = string.Equals(toolName, "ansight_call_app_tool", StringComparison.Ordinal)
            && arguments["toolId"] is JsonValue toolIdValue
            && toolIdValue.TryGetValue<string>(out var toolIdText) ? toolIdText : null;
        var capturedArguments = request.CaptureTrace
            ? RepositoryTaskCallTrace.CaptureArguments(toolName, message["arguments"], traceAppToolId)
            : null;
        IReadOnlyList<RepositoryTaskToolCall>? childCalls = null;
        IReadOnlyList<RepositoryTaskAssertion>? childAssertions = null;
        RepositoryTaskSourceTrace? sourceTrace = null;
        RequestResult result;
        using var cancellationScope = ToolExecutionCancellation.Push(cancellationToken);
        using var fixtureScope = AudioFixtureScope.Push(request.Task.RepositoryRootPath);
        try
        {
            var audioRejection = AudioExecutionPolicy.RejectParallelCall(toolName, request.OperationContext);
            if (audioRejection is not null)
            {
                result = audioRejection;
            }
            else if (isTaskMethod)
            {
                var taskApiResult = await taskApiExecutor!(
                        request,
                        toolName,
                        arguments,
                        callCorrelationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                result = taskApiResult.Result;
                childCalls = request.CaptureTrace ? taskApiResult.ChildCalls : null;
                childAssertions = request.CaptureTrace ? taskApiResult.Assertions : null;
                sourceTrace = request.CaptureTrace ? taskApiResult.SourceTrace : null;
            }
            else
            {
                var operationTask = toolExecutor(toolName, arguments, callCorrelationId);
                // Audio owns bounded cancellation cleanup and the microphone route lease.
                // Wait for it to stop playback before completing the task's call.
                result = string.Equals(toolName, "ansight_inject_audio", StringComparison.Ordinal)
                    ? await operationTask.ConfigureAwait(false)
                    : await operationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            RecordCall(true, "Call cancelled before completion.", new JsonObject
            {
                ["isError"] = true,
                ["cancelled"] = true,
                ["message"] = "Call cancelled before completion.",
                ["exceptionType"] = exception.GetType().Name
            });
            throw;
        }
        catch (Exception exception)
        {
            RecordCall(true, exception.Message, new JsonObject
            {
                ["isError"] = true,
                ["message"] = exception.Message,
                ["exceptionType"] = exception.GetType().Name
            });
            await WriteCallResultAsync(
                process,
                callId,
                false,
                null,
                exception.Message,
                "tool",
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        stopwatch.Stop();
        var payload = result.Payload;
        var isToolError = result.IsError
                          || (payload?["isError"] is JsonValue errorValue
                              && errorValue.TryGetValue<bool>(out var parsedError)
                              && parsedError);
        var structuredContent = payload?["structuredContent"]?.DeepClone()
                                ?? payload?.DeepClone()
                                ?? new JsonObject();
        var originalStructuredContent = structuredContent;
        var resultText = structuredContent.ToJsonString(JsonUtil.Compact);
        var messageText = result.ErrorMessage
                          ?? structuredContent["message"]?.GetValue<string>()
                          ?? (isToolError ? ReadAppToolErrorMessage(structuredContent) : null)
                          ?? (isToolError ? $"{auditName} failed." : $"{auditName} completed.");
        if (resultText.Length > MaximumToolResultCharacters)
        {
            isToolError = true;
            structuredContent = new JsonObject
            {
                ["truncated"] = true,
                ["originalCharacterCount"] = resultText.Length
            };
            messageText = $"{auditName} result exceeded the task runtime limit.";
        }

        RecordCall(isToolError, messageText, result.IsError && result.Payload is null
            ? new JsonObject
            {
                ["isError"] = true,
                ["errorCode"] = result.ErrorCode,
                ["message"] = messageText
            }
            : originalStructuredContent);
        await WriteCallResultAsync(
            process,
            callId,
            !isToolError,
            structuredContent,
            messageText,
            requestedName.StartsWith("ansight_assert_", StringComparison.Ordinal)
                ? "assertion"
                : "tool",
            cancellationToken).ConfigureAwait(false);
        return null;

        void RecordCall(bool isError, string callMessage, JsonNode? callResult)
        {
            stopwatch.Stop();
            toolCalls.Add(new RepositoryTaskToolCall(
                toolCalls.Count + 1,
                auditName,
                startedAtUtc,
                stopwatch.ElapsedMilliseconds,
                isError,
                callMessage)
            {
                CompletedAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = callCorrelationId,
                Arguments = capturedArguments,
                Result = request.CaptureTrace ? RepositoryTaskCallTrace.CaptureResult(callResult, traceAppToolId) : null,
                ChildCalls = childCalls,
                Assertions = childAssertions,
                SourceTrace = sourceTrace
            });
        }
    }

    private static string? ReadAppToolErrorMessage(JsonNode structuredContent)
    {
        var payload = structuredContent["payload"] as JsonObject ?? structuredContent as JsonObject;
        var error = payload?["error"] as JsonObject;
        var message = ReadString(error?["message"]) ?? ReadString(payload?["message"]);
        var code = ReadString(error?["code"]) ?? ReadString(payload?["code"]);
        return code is null
            ? message
            : message is null ? code : $"{code}: {message}";
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static async Task WriteCallResultAsync(
        Process process,
        string callId,
        bool succeeded,
        JsonNode? result,
        string message,
        string failureKind,
        CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["type"] = "callResult",
            ["callId"] = callId,
            ["ok"] = succeeded,
            ["result"] = result?.DeepClone(),
            ["message"] = message,
            ["failureKind"] = failureKind
        };
        await process.StandardInput.WriteLineAsync(response.ToJsonString(JsonUtil.Compact))
            .ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private ProcessStartInfo CreateStartInfo(RepositoryTaskDefinition task, bool captureTrace)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = task.RepositoryRootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetExtension(task.ModulePath), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--experimental-strip-types");
        }

        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("--eval");
        startInfo.ArgumentList.Add(bootstrapSource);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(task.ModulePath);
        startInfo.ArgumentList.Add(captureTrace ? "trace" : "");
        startInfo.ArgumentList.Add(task.RepositoryRootPath);

        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        var inheritedTemp = Environment.GetEnvironmentVariable("TMPDIR")
                            ?? Environment.GetEnvironmentVariable("TEMP")
                            ?? Path.GetTempPath();
        var inheritedSystemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(inheritedPath))
        {
            startInfo.Environment["PATH"] = inheritedPath;
        }

        startInfo.Environment["TMPDIR"] = inheritedTemp;
        startInfo.Environment["TEMP"] = inheritedTemp;
        startInfo.Environment["TMP"] = inheritedTemp;
        startInfo.Environment["ANSIGHT_TASK"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";
        if (!string.IsNullOrWhiteSpace(inheritedSystemRoot))
        {
            startInfo.Environment["SystemRoot"] = inheritedSystemRoot;
        }

        return startInfo;
    }

    private JsonObject CreateInvocation(RepositoryTaskExecutionRequest request)
        => new()
        {
            ["schema"] = "ansight.repository-task.v2",
            ["capabilities"] = request.Capabilities?.DeepClone(),
            ["run"] = new JsonObject
            {
                ["executionMode"] = request.Capabilities?["executionMode"]?.DeepClone() ?? JsonValue.Create("sdk"),
                ["runId"] = request.RunId,
                ["taskId"] = request.Task.TaskId,
                ["appId"] = request.Task.AppId,
                ["sessionId"] = request.SessionId,
                ["repositoryRootPath"] = request.Task.RepositoryRootPath,
                ["timeoutSeconds"] = request.Task.Timeout.TotalSeconds,
                ["maximumActions"] = request.Task.MaximumActions
            },
            ["input"] = request.Input.DeepClone(),
            ["secrets"] = new JsonObject(request.SecretValues.Select(secret =>
                new KeyValuePair<string, JsonNode?>(secret.Key, JsonValue.Create(secret.Value)))),
            ["hostSuites"] = SerializeSuites(hostApiSuites),
            ["taskSuites"] = SerializeSuites(RepositoryJavaScriptApiMethods.TaskCompositionSuites),
            ["appSuites"] = SerializeSuites(RepositoryJavaScriptApiMethods.StandardAppToolSuites)
        };

    private static void ApplySessionTarget(JsonObject arguments, string sessionId)
    {
        RemoveTargetOverrides(arguments);
        arguments["sessionId"] = sessionId;
    }

    private static void RemoveTargetOverrides(JsonObject arguments)
    {
        arguments.Remove("sessionId");
        arguments.Remove("appId");
        arguments.Remove("deviceId");
        arguments.Remove("bundleIdentifier");
    }

    private static JsonObject SerializeMethods(IReadOnlyDictionary<string, string> methods)
        => new(methods.Select(method => new KeyValuePair<string, JsonNode?>(
            method.Key,
            JsonValue.Create(method.Value))));

    private static JsonObject SerializeSuites(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> suites)
        => new(suites.Select(suite => new KeyValuePair<string, JsonNode?>(
            suite.Key,
            SerializeMethods(suite.Value))));

    private static IReadOnlyList<RepositoryTaskAssertion> ParseAssertions(JsonArray? array)
    {
        if (array is null)
        {
            return [];
        }

        var assertions = new List<RepositoryTaskAssertion>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var assertionId = item["assertionId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(assertionId))
            {
                continue;
            }

            assertions.Add(new RepositoryTaskAssertion(
                assertionId.Trim(),
                item["passed"]?.GetValue<bool>() ?? false,
                item["message"]?.GetValue<string>() ?? "Assertion completed.",
                item["expected"]?.DeepClone(),
                item["actual"]?.DeepClone())
            {
                Matcher = item["matcher"]?.GetValue<string>(),
                CompletedAtUtc = DateTimeOffset.TryParse(item["completedAtUtc"]?.GetValue<string>(), out var completed)
                    ? completed : null
            });
        }

        return assertions;
    }

    private static RepositoryTaskRunStatus ParseStatus(string? status)
        => status switch
        {
            "passed" => RepositoryTaskRunStatus.Passed,
            "failed" => RepositoryTaskRunStatus.Failed,
            "inconclusive" => RepositoryTaskRunStatus.Inconclusive,
            _ => RepositoryTaskRunStatus.Error
        };

    private static RepositoryTaskRunResult Finish(
        RepositoryTaskExecutionRequest request,
        RepositoryTaskRunStatus status,
        DateTimeOffset startedAtUtc,
        Stopwatch stopwatch,
        string message,
        JsonNode? output,
        IReadOnlyList<RepositoryTaskAssertion> assertions,
        IReadOnlyCollection<RepositoryTaskToolCall> toolCalls,
        string standardError)
    {
        stopwatch.Stop();
        return new RepositoryTaskRunResult(
            request.RunId,
            request.Task.RepositoryRootPath,
            request.Task.AppId,
            request.SessionId,
            request.Task.TaskId,
            status,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            stopwatch.ElapsedMilliseconds,
            message,
            request.Input.DeepClone().AsObject(),
            output?.DeepClone(),
            assertions.ToArray(),
            toolCalls.ToArray(),
            standardError)
        {
            SourceTrace = request.SourceCapture?.Finish(request.Task.TaskId)
        };
    }

    private static bool TryParseMessage(string? text, out JsonObject? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            message = JsonNode.Parse(text) as JsonObject;
            return message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4_096];
        var builder = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            var retainedCount = Math.Min(count, maximumCharacters - builder.Length);
            if (retainedCount > 0)
            {
                builder.Append(buffer, 0, retainedCount);
            }
        }

        return builder.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A process that never started has no exit to observe.
        }
    }

    private readonly record struct RepositoryTaskCallFailure(
        RepositoryTaskRunStatus Status,
        string Message);
}
