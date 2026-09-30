using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;

namespace Ansight.Host.Runtime.Automation;

internal sealed class JavaScriptRepositoryAutomationExecutor : IRepositoryAutomationExecutor
{
    private static readonly TimeSpan moduleLoadTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumStandardOutputCharacters = 1_048_576;
    private const int MaximumStandardErrorCharacters = 65_536;
    private const int MaximumHandshakeCharacters = 1_024;
    private static readonly string bootstrapSource = EmbeddedTextResource
        .Read("Runtime/Automation/Resources/repository-automation-bootstrap.mjs")
        .TrimEnd();

    private readonly RepositoryTaskToolExecutor? hostTools;
    private readonly string executablePath;
    private readonly AppToolRepositoryAutomationExecutor appToolExecutor;
    private readonly string? unavailableMessage;

    public JavaScriptRepositoryAutomationExecutor(
        string executablePath,
        AppToolRepositoryAutomationExecutor appToolExecutor,
        string? unavailableMessage = null,
        RepositoryTaskToolExecutor? hostTools = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("A Node.js executable path is required.", nameof(executablePath));
        }

        this.hostTools = hostTools;
        this.executablePath = executablePath.Trim();
        this.appToolExecutor = appToolExecutor ?? throw new ArgumentNullException(nameof(appToolExecutor));
        this.unavailableMessage = string.IsNullOrWhiteSpace(unavailableMessage)
            ? null
            : unavailableMessage.Trim();
    }

    public async Task<RepositoryAutomationExecutionResult> ExecuteAsync(
        RepositoryAutomationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAtUtc = DateTimeOffset.UtcNow;
        if (unavailableMessage is not null)
        {
            return new RepositoryAutomationExecutionResult(
                AutomationRunStatus.Rejected,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                ExitCode: null,
                unavailableMessage,
                Output: null,
                StandardError: string.Empty);
        }

        Process? process = null;

        try
        {
            JsonObject? capabilities = null;
            if (hostTools is not null && request.Event.SessionId is { } sessionId)
            {
                using var preflight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                preflight.CancelAfter(TimeSpan.FromSeconds(15));
                using var scope = ToolExecutionCancellation.Push(preflight.Token);
                var response = await hostTools("ansight_get_execution_capabilities",
                    new JsonObject { ["sessionId"] = sessionId }, request.Event.CorrelationId).ConfigureAwait(false);
                capabilities = response.Payload?["structuredContent"]?.DeepClone() as JsonObject;
                var missing = capabilities is null ? ExecutionCapabilities.Unavailable("session", null, "The event session is unavailable.")
                    : ExecutionCapabilities.Missing(capabilities, request.Trigger.Requires, request.Trigger.SchemaVersion < 2);
                if (missing is not null)
                    return new RepositoryAutomationExecutionResult(AutomationRunStatus.Rejected, startedAtUtc,
                        DateTimeOffset.UtcNow, null, missing["message"]!.GetValue<string>(), missing, string.Empty);
            }
            else if (request.Trigger.Requires is { } requirements && (requirements.Capabilities.Count > 0 || requirements.AppTools.Count > 0))
                return new RepositoryAutomationExecutionResult(AutomationRunStatus.Rejected, startedAtUtc, DateTimeOffset.UtcNow,
                    null, "The trigger host cannot resolve session capabilities.", null, string.Empty);
            process = Process.Start(CreateStartInfo(request));
            if (process is null)
            {
                return Failure(startedAtUtc, "The Node.js runtime did not start.");
            }

            var standardErrorTask = ReadBoundedTextAsync(
                process.StandardError,
                MaximumStandardErrorCharacters,
                CancellationToken.None);

            BoundedTextReadResult handshake;
            using (var moduleLoadTimeoutCts = new CancellationTokenSource(moduleLoadTimeout))
            using (var moduleLoadCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       moduleLoadTimeoutCts.Token))
            {
                try
                {
                    handshake = await ReadBoundedLineAsync(
                        process.StandardOutput,
                        MaximumHandshakeCharacters,
                        moduleLoadCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    await ObserveExitAsync(process).ConfigureAwait(false);
                    var moduleLoadError = await standardErrorTask.ConfigureAwait(false);
                    return ModuleLoadCancelled(
                        startedAtUtc,
                        TryGetExitCode(process),
                        cancellationToken.IsCancellationRequested,
                        moduleLoadError.Text);
                }
            }

            if (handshake.ExceededLimit
                || !TryParseOutput(handshake.Text, out var handshakeOutput)
                || !string.Equals(handshakeOutput?["type"]?.GetValue<string>(), "ready", StringComparison.Ordinal))
            {
                TryKill(process);
                await ObserveExitAsync(process).ConfigureAwait(false);
                var handshakeError = await standardErrorTask.ConfigureAwait(false);
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Failed,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    TryGetExitCode(process),
                    handshake.ExceededLimit
                        ? $"Repository automation startup output exceeded {MaximumHandshakeCharacters} characters."
                        : "The trigger module did not complete the Ansight startup handshake.",
                    null,
                    handshakeError.Text);
            }

            var invocation = CreateInput(request);
            invocation["capabilities"] = capabilities?.DeepClone();
            invocation["run"]!["executionMode"] = capabilities?["executionMode"]?.DeepClone();
            var input = invocation.ToJsonString(JsonUtil.Compact);
            BoundedTextReadResult standardOutput;

            using (var functionTimeoutCts = new CancellationTokenSource(request.Trigger.Automation.FunctionTimeout))
            using (var functionCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       functionTimeoutCts.Token))
            {
                var standardOutputTask = ReadBoundedTextAsync(
                    process.StandardOutput,
                    MaximumStandardOutputCharacters,
                    functionCts.Token);
                try
                {
                    await process.StandardInput.WriteAsync(input.AsMemory(), functionCts.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(functionCts.Token).ConfigureAwait(false);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(functionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    await ObserveExitAsync(process).ConfigureAwait(false);
                    var cancelledOutput = await standardOutputTask.ConfigureAwait(false);
                    var cancelledError = await standardErrorTask.ConfigureAwait(false);
                    return FunctionCancelled(
                        request,
                        startedAtUtc,
                        TryGetExitCode(process),
                        cancellationToken.IsCancellationRequested,
                        cancelledOutput,
                        cancelledError.Text);
                }

                standardOutput = await standardOutputTask.ConfigureAwait(false);
            }

            var standardError = await standardErrorTask.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return FunctionCancelled(
                    request,
                    startedAtUtc,
                    TryGetExitCode(process),
                    hostCancellationRequested: true,
                    standardOutput,
                    standardError.Text);
            }

            if (standardOutput.ExceededLimit)
            {
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Failed,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    process.ExitCode,
                    $"Repository automation output exceeded {MaximumStandardOutputCharacters} characters.",
                    null,
                    standardError.Text);
            }

            if (process.ExitCode != 0)
            {
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Failed,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    process.ExitCode,
                    $"Repository automation exited with code {process.ExitCode}.",
                    TryParseOutput(standardOutput.Text, out var failedOutput) ? failedOutput : null,
                    standardError.Text);
            }

            if (!TryParseOutput(standardOutput.Text, out var output)
                || output is null
                || !string.Equals(output["type"]?.GetValue<string>(), "result", StringComparison.Ordinal))
            {
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Failed,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    process.ExitCode,
                    "Repository trigger modules must return through the Ansight TypeScript host contract.",
                    null,
                    standardError.Text);
            }

            if (output["error"] is JsonObject capabilityError && capabilityError["code"]?.GetValue<string>() == ExecutionCapabilities.ErrorCode)
                return new RepositoryAutomationExecutionResult(AutomationRunStatus.Rejected, startedAtUtc, DateTimeOffset.UtcNow,
                    process.ExitCode, capabilityError["message"]!.GetValue<string>(), capabilityError.DeepClone().AsObject(), standardError.Text);

            if (!output.TryGetPropertyValue("action", out var actionNode) || actionNode is null)
            {
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Succeeded,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    process.ExitCode,
                    "Repository automation completed without a host action.",
                    output,
                    standardError.Text);
            }

            if (actionNode is not JsonObject action)
            {
                return ContractFailure(
                    startedAtUtc,
                    process.ExitCode,
                    "Repository trigger modules must return one host action or no value.",
                    output,
                    standardError.Text);
            }

            if (action["type"]?.GetValue<string>() == "hostTool")
                return await ExecuteHostActionAsync(request, action, startedAtUtc, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(action["type"]?.GetValue<string>(), "appTool", StringComparison.Ordinal))
            {
                return ContractFailure(
                    startedAtUtc,
                    process.ExitCode,
                    "The returned host action type is not supported.",
                    output,
                    standardError.Text);
            }

            var toolId = action["toolId"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(toolId))
            {
                return new RepositoryAutomationExecutionResult(
                    AutomationRunStatus.Rejected,
                    startedAtUtc,
                    DateTimeOffset.UtcNow,
                    process.ExitCode,
                    "The returned app tool ID must be non-empty.",
                    output,
                    standardError.Text);
            }

            if (action["arguments"] is not (null or JsonObject))
            {
                return ContractFailure(
                    startedAtUtc,
                    process.ExitCode,
                    "The returned app-tool arguments must be an object.",
                    output,
                    standardError.Text);
            }

            RepositoryAutomationExecutionResult appToolResult;
            using (var actionTimeoutCts = new CancellationTokenSource(request.Trigger.Automation.ActionTimeout))
            using (var actionCts = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       actionTimeoutCts.Token))
            {
                appToolResult = await appToolExecutor.ExecuteActionAsync(
                    request,
                    toolId,
                    action["arguments"] as JsonObject,
                    startedAtUtc,
                    actionCts.Token,
                    cancellationToken).ConfigureAwait(false);
            }
            var compositeOutput = new JsonObject
            {
                ["action"] = action.DeepClone(),
                ["result"] = appToolResult.Output?.DeepClone()
            };
            return appToolResult with
            {
                ExitCode = process.ExitCode,
                Output = compositeOutput,
                StandardError = standardError.Text
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or System.ComponentModel.Win32Exception)
        {
            TryKill(process);
            return Failure(startedAtUtc, $"Repository automation could not execute: {exception.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<RepositoryAutomationExecutionResult> ExecuteHostActionAsync(
        RepositoryAutomationExecutionRequest request, JsonObject action, DateTimeOffset started, CancellationToken cancellationToken)
    {
        var name = action["toolName"]?.GetValue<string>();
        if (hostTools is null || request.Trigger.SchemaVersion < 2 || request.Event.SessionId is not { } sessionId
            || name is not ("ansight_take_screenshot" or "ansight_capture_sandbox_file" or "ansight_create_annotation")
            || action["arguments"] is not (null or JsonObject))
            return new RepositoryAutomationExecutionResult(AutomationRunStatus.Rejected, started, DateTimeOffset.UtcNow,
                null, "The trigger requested an unsupported host evidence action.", null, string.Empty);
        var arguments = action["arguments"]?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var key in new[] { "sessionId", "appId", "deviceId", "bundleIdentifier" }) arguments.Remove(key);
        arguments["sessionId"] = sessionId;
        if (name == "ansight_create_annotation") arguments["source"] = "trigger:" + request.Trigger.TriggerId;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Trigger.Automation.ActionTimeout);
        using var scope = ToolExecutionCancellation.Push(deadline.Token);
        try
        {
            var response = await hostTools(name, arguments, request.Event.CorrelationId).WaitAsync(deadline.Token).ConfigureAwait(false);
            var result = response.Payload?["structuredContent"]?.DeepClone() as JsonObject;
            var failed = response.IsError || response.Payload?["isError"]?.GetValue<bool>() == true;
            var status = result?["code"]?.GetValue<string>() == ExecutionCapabilities.ErrorCode ? AutomationRunStatus.Rejected
                : failed ? AutomationRunStatus.Failed : AutomationRunStatus.Succeeded;
            return new RepositoryAutomationExecutionResult(status, started, DateTimeOffset.UtcNow, null,
                result?["message"]?.GetValue<string>() ?? (failed ? "Host evidence action failed." : "Host evidence captured."), result, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new RepositoryAutomationExecutionResult(cancellationToken.IsCancellationRequested ? AutomationRunStatus.Cancelled : AutomationRunStatus.TimedOut,
                started, DateTimeOffset.UtcNow, null, "Host evidence action was cancelled or timed out.", null, string.Empty);
        }
    }

    private ProcessStartInfo CreateStartInfo(RepositoryAutomationExecutionRequest request)
    {
        var modulePath = request.Trigger.Automation.EntrypointPath
                         ?? throw new InvalidOperationException("The TypeScript automation does not declare a module path.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = request.Trigger.RepositoryRootPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetExtension(modulePath), ".ts", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--no-warnings");
            startInfo.ArgumentList.Add("--experimental-strip-types");
        }

        startInfo.ArgumentList.Add("--input-type=module");
        startInfo.ArgumentList.Add("--eval");
        startInfo.ArgumentList.Add(bootstrapSource);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(modulePath);

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
        startInfo.Environment["ANSIGHT_AUTOMATION"] = "1";
        startInfo.Environment["NO_COLOR"] = "1";
        if (!string.IsNullOrWhiteSpace(inheritedSystemRoot))
        {
            startInfo.Environment["SystemRoot"] = inheritedSystemRoot;
        }

        return startInfo;
    }

    private static JsonObject CreateInput(RepositoryAutomationExecutionRequest request)
    {
        return new JsonObject
        {
            ["schema"] = "ansight.javascript-run.v1",
            ["run"] = new JsonObject
            {
                ["runId"] = request.RunId,
                ["attemptNumber"] = request.AttemptNumber,
                ["maximumAttempts"] = request.Trigger.Automation.RetryPolicy.MaximumAttempts,
                ["triggerId"] = request.Trigger.TriggerId,
                ["repositoryRootPath"] = request.Trigger.RepositoryRootPath,
                ["enqueuedAtUtc"] = request.EnqueuedAtUtc,
                ["functionTimeoutMs"] = request.Trigger.Automation.FunctionTimeout.TotalMilliseconds,
                ["actionTimeoutSeconds"] = request.Trigger.Automation.ActionTimeout.TotalSeconds
            },
            ["event"] = JsonSerializer.SerializeToNode(request.Event, JsonUtil.Compact),
            ["appSuites"] = new JsonObject(
                RepositoryJavaScriptApiMethods.StandardAppToolSuites.Select(suite =>
                    new KeyValuePair<string, JsonNode?>(
                        suite.Key,
                        new JsonObject(suite.Value.Select(method =>
                            new KeyValuePair<string, JsonNode?>(method.Key, JsonValue.Create(method.Value)))))))
        };
    }

    private static RepositoryAutomationExecutionResult ModuleLoadCancelled(
        DateTimeOffset startedAtUtc,
        int? exitCode,
        bool hostCancellationRequested,
        string standardError)
    {
        var status = hostCancellationRequested
            ? AutomationRunStatus.Cancelled
            : AutomationRunStatus.TimedOut;
        var message = hostCancellationRequested
            ? "Repository automation execution was cancelled while loading its module."
            : $"Trigger module loading exceeded the host's {moduleLoadTimeout.TotalSeconds:0}-second limit.";
        return new RepositoryAutomationExecutionResult(
            status,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            exitCode,
            message,
            null,
            standardError);
    }

    private static RepositoryAutomationExecutionResult FunctionCancelled(
        RepositoryAutomationExecutionRequest request,
        DateTimeOffset startedAtUtc,
        int? exitCode,
        bool hostCancellationRequested,
        BoundedTextReadResult standardOutput,
        string standardError)
    {
        var status = hostCancellationRequested
            ? AutomationRunStatus.Cancelled
            : AutomationRunStatus.TimedOut;
        var message = hostCancellationRequested
            ? "Repository automation execution was cancelled."
            : $"Trigger function exceeded its "
              + $"{request.Trigger.Automation.FunctionTimeout.TotalMilliseconds:0}-millisecond execution bound.";
        return new RepositoryAutomationExecutionResult(
            status,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            exitCode,
            message,
            !standardOutput.ExceededLimit
            && TryParseOutput(standardOutput.Text, out var parsedOutput)
                ? parsedOutput
                : null,
            standardError);
    }

    private static async Task<BoundedTextReadResult> ReadBoundedLineAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var character = new char[1];
        var text = new StringBuilder(Math.Min(maximumCharacters, 128));
        var exceededLimit = false;
        while (true)
        {
            var charactersRead = await reader.ReadAsync(
                character.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (charactersRead == 0 || character[0] == '\n')
            {
                break;
            }

            if (character[0] == '\r')
            {
                continue;
            }

            if (text.Length < maximumCharacters)
            {
                text.Append(character[0]);
            }
            else
            {
                exceededLimit = true;
            }
        }

        return new BoundedTextReadResult(text.ToString(), exceededLimit);
    }

    private static bool TryParseOutput(string output, out JsonObject? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            parsed = JsonNode.Parse(output) as JsonObject;
            return parsed is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static RepositoryAutomationExecutionResult ContractFailure(
        DateTimeOffset startedAtUtc,
        int? exitCode,
        string message,
        JsonObject? output,
        string standardError)
        => new(
            AutomationRunStatus.Failed,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            exitCode,
            message,
            output,
            standardError);

    private static RepositoryAutomationExecutionResult Failure(
        DateTimeOffset startedAtUtc,
        string message)
        => new(
            AutomationRunStatus.Failed,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            ExitCode: null,
            message,
            Output: null,
            StandardError: string.Empty);

    private static async Task<BoundedTextReadResult> ReadBoundedTextAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var text = new StringBuilder(Math.Min(maximumCharacters, buffer.Length));
        var exceededLimit = false;

        while (true)
        {
            int charactersRead;
            try
            {
                charactersRead = await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (charactersRead == 0)
            {
                break;
            }

            var remainingCapacity = maximumCharacters - text.Length;
            if (remainingCapacity > 0)
            {
                text.Append(buffer, 0, Math.Min(charactersRead, remainingCapacity));
            }

            if (charactersRead > remainingCapacity)
            {
                exceededLimit = true;
            }
        }

        return new BoundedTextReadResult(text.ToString(), exceededLimit);
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
        }
    }
}
