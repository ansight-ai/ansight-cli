using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.Operations;

namespace Ansight.Host.Runtime.Tasks;

internal sealed class RepositoryTaskRouter
{
    private const int MaximumTaskCallDepth = 8;
    private readonly Lock gate = new();
    private readonly ProductAnalytics analytics;
    private readonly RepositoryTaskRunStore runStore;
    private readonly Dictionary<string, RepositoryTaskHostToolDescriptor> hostToolsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> hostApiSuites = new(StringComparer.Ordinal);
    private Func<string, JsonObject, string?, OperationExecutionContext?, Task<RequestResult>>? toolExecutor;
    private bool hostToolValidationConfigured;
    internal Action<string, string, string, string>? PublishEvent { get; set; }
    private JavaScriptRuntimeResolution runtime = JavaScriptRuntimeResolver.Resolve("node");

    public RepositoryTaskRouter(string applicationDataPath)
        : this(applicationDataPath, new ProductAnalytics(applicationDataPath))
    {
    }

    public RepositoryTaskRouter(IApplicationPaths applicationPaths)
        : this(
            applicationPaths?.ApplicationDataPath
            ?? throw new ArgumentNullException(nameof(applicationPaths)),
            ProductAnalytics.For(applicationPaths))
    {
    }

    private RepositoryTaskRouter(string applicationDataPath, ProductAnalytics analytics)
    {
        this.analytics = analytics;
        runStore = new RepositoryTaskRunStore(applicationDataPath);
    }

    public void ConfigureToolExecutor(RepositoryTaskToolExecutor executor)
        => ConfigureToolExecutor((name, arguments, correlationId, _) => executor(name, arguments, correlationId));

    public void ConfigureToolExecutor(Func<string, JsonObject, string?, OperationExecutionContext?, Task<RequestResult>> executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        lock (gate)
        {
            toolExecutor = executor;
        }
    }

    public void ConfigureHostToolRegistry(OperationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        lock (gate)
        {
            foreach (var descriptor in registry.GetRepositoryTaskTools())
            {
                if (hostToolsByName.TryGetValue(descriptor.ToolName, out var existingDescriptor))
                {
                    if (existingDescriptor != descriptor)
                    {
                        throw new InvalidOperationException(
                            $"Host tool '{descriptor.ToolName}' has conflicting repository-task registrations.");
                    }

                    continue;
                }

                if (!hostApiSuites.TryGetValue(descriptor.ApiFeatureName, out var featureMethods))
                {
                    featureMethods = new Dictionary<string, string>(StringComparer.Ordinal);
                    hostApiSuites.Add(descriptor.ApiFeatureName, featureMethods);
                }

                if (featureMethods.TryGetValue(descriptor.ApiMethodName, out var existingToolName))
                {
                    throw new InvalidOperationException(
                        $"Repository task API method '{descriptor.ApiFeatureName}.{descriptor.ApiMethodName}' maps to both " +
                        $"'{existingToolName}' and '{descriptor.ToolName}'.");
                }

                hostToolsByName.Add(descriptor.ToolName, descriptor);
                featureMethods.Add(descriptor.ApiMethodName, descriptor.ToolName);
            }

            hostToolValidationConfigured = true;
        }
    }

    public RepositoryTaskLoadResult Load(string repositoryPath, string appId)
    {
        bool validationConfigured;
        IReadOnlyDictionary<string, RepositoryTaskHostToolDescriptor> currentHostTools;
        lock (gate)
        {
            validationConfigured = hostToolValidationConfigured;
            currentHostTools = new Dictionary<string, RepositoryTaskHostToolDescriptor>(
                hostToolsByName,
                StringComparer.Ordinal);
        }

        if (!validationConfigured)
        {
            return new RepositoryTaskLoadResult(
                [],
                ["Repository task host-tool validation is not configured."]);
        }

        return RepositoryTaskLoader.Load(
            repositoryPath,
            appId,
            currentHostTools.GetValueOrDefault);
    }

    public void ConfigureRuntime(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        lock (gate)
        {
            runtime = JavaScriptRuntimeResolver.Resolve(executablePath.Trim());
        }
    }

    public Task<RepositoryTaskRunResult> ExecuteAsync(
        RepositoryTaskDefinition task,
        string sessionId,
        JsonObject input,
        string? correlationId,
        CancellationToken cancellationToken,
        OperationExecutionContext? context = null,
        IReadOnlyDictionary<string, string>? secretValues = null)
        => analytics.ObserveUsageAsync("task", () => ExecuteCoreAsync(
            task,
            sessionId,
            input,
            correlationId,
            [task.TaskId],
            RepositoryTaskTraceScope.IsEnabled,
            cancellationToken, context, secretValues), result => result.Status.ToString().ToLowerInvariant() switch
            {
                "succeeded" => "succeeded", "cancelled" => "cancelled", "rejected" => "blocked", _ => "failed"
            });

    private async Task<RepositoryTaskRunResult> ExecuteCoreAsync(
        RepositoryTaskDefinition task,
        string sessionId,
        JsonObject input,
        string? correlationId,
        IReadOnlyList<string> taskCallChain,
        bool captureTrace,
        CancellationToken cancellationToken,
        OperationExecutionContext? context,
        IReadOnlyDictionary<string, string>? secretValues)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(taskCallChain);

        Func<string, JsonObject, string?, OperationExecutionContext?, Task<RequestResult>>? currentToolExecutor;
        JavaScriptRuntimeResolution currentRuntime;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> currentHostApiSuites;
        lock (gate)
        {
            currentToolExecutor = toolExecutor;
            currentRuntime = runtime;
            currentHostApiSuites = hostApiSuites.ToDictionary(
                suite => suite.Key,
                suite => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    suite.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        }

        var runId = Guid.NewGuid().ToString("N");
        RepositoryTaskRunResult result;
        if (!task.Enabled)
        {
            var now = DateTimeOffset.UtcNow;
            result = new RepositoryTaskRunResult(
                runId,
                task.RepositoryRootPath,
                task.AppId,
                sessionId.Trim(),
                task.TaskId,
                RepositoryTaskRunStatus.Rejected,
                now,
                now,
                0,
                $"Repository task '{task.TaskId}' is disabled.",
                input.DeepClone().AsObject(),
                null,
                [],
                [],
                string.Empty);
        }
        else if (currentToolExecutor is null)
        {
            var now = DateTimeOffset.UtcNow;
            result = new RepositoryTaskRunResult(
                runId,
                task.RepositoryRootPath,
                task.AppId,
                sessionId.Trim(),
                task.TaskId,
                RepositoryTaskRunStatus.Rejected,
                now,
                now,
                0,
                "Repository task execution is not configured.",
                input.DeepClone().AsObject(),
                null,
                [],
                [],
                string.Empty);
        }
        else
        {
            var executor = new JavaScriptRepositoryTaskExecutor(
                currentRuntime.ExecutablePath,
                (name, arguments, callId) => currentToolExecutor(name, arguments, callId, context),
                currentRuntime.IsAvailable ? null : currentRuntime.Message,
                currentHostApiSuites,
                ExecuteTaskApiAsync,
                currentHostApiSuites.ContainsKey("capabilities") ? async (id, token) =>
                {
                    using var scope = ToolExecutionCancellation.Push(token);
                    var resolved = await currentToolExecutor("ansight_get_execution_capabilities",
                        new JsonObject { ["sessionId"] = id }, correlationId, context).ConfigureAwait(false);
                    return resolved.Payload?["structuredContent"]?.DeepClone().AsObject()
                        ?? throw new InvalidOperationException(resolved.ErrorMessage ?? "Execution capabilities are unavailable.");
                } : null);
            PublishEvent?.Invoke(sessionId, "task.started", "host.task.started", task.TaskId);
            result = await executor.ExecuteAsync(
                    new RepositoryTaskExecutionRequest(
                        runId,
                        task,
                        sessionId.Trim(),
                        input.DeepClone().AsObject(),
                        string.IsNullOrWhiteSpace(correlationId)
                            ? $"repository-task-{runId}"
                            : correlationId.Trim())
                    {
                        TaskCallChain = taskCallChain.ToArray(),
                        CaptureTrace = captureTrace,
                        OperationContext = context,
                        SecretValues = secretValues ?? new Dictionary<string, string>()
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var outcome = result.Status is RepositoryTaskRunStatus.Passed or RepositoryTaskRunStatus.Inconclusive
                ? "completed" : result.Status.ToString().ToLowerInvariant();
            PublishEvent?.Invoke(sessionId, "task." + outcome, "host.task." + outcome, task.TaskId);
        }

        try
        {
            runStore.Append(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result with
            {
                Message = $"{result.Message} Task audit could not be persisted: {exception.Message}"
            };
        }

        return result;
    }

    private async Task<RepositoryTaskApiResult> ExecuteTaskApiAsync(
        RepositoryTaskExecutionRequest parentRequest,
        string methodName,
        JsonObject arguments,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);

        var loadResult = Load(
            parentRequest.Task.RepositoryRootPath,
            parentRequest.Task.AppId);
        if (string.Equals(methodName, "list", StringComparison.Ordinal))
        {
            var query = NormalizeOptionalString(arguments["query"]);
            var feature = NormalizeOptionalString(arguments["feature"]);
            var maximumResults = ReadMaximumResults(arguments["maxResults"]);
            return new RepositoryTaskApiResult(RequestResult.ToolResult(
                RepositoryTaskProtocol.BuildDiscoveryResult(
                    loadResult,
                    parentRequest.Task.AppId,
                    parentRequest.SessionId,
                    query,
                    feature,
                    maximumResults),
                isError: false));
        }

        if (!string.Equals(methodName, "run", StringComparison.Ordinal))
        {
            return TaskApiError($"Unknown task composition method '{methodName}'.");
        }

        var taskId = NormalizeOptionalString(arguments["taskId"]);
        if (taskId is null)
        {
            return TaskApiError("taskId is required.");
        }
        if (parentRequest.TaskCallChain.Contains(taskId, StringComparer.Ordinal))
        {
            var cycle = string.Join(" -> ", parentRequest.TaskCallChain.Append(taskId));
            return TaskApiError($"Recursive repository task call rejected: {cycle}.");
        }
        if (parentRequest.TaskCallChain.Count >= MaximumTaskCallDepth)
        {
            return TaskApiError(
                $"Repository task calls cannot exceed {MaximumTaskCallDepth} task execution levels.");
        }

        var task = loadResult.Tasks.FirstOrDefault(candidate => string.Equals(
            candidate.TaskId,
            taskId,
            StringComparison.Ordinal));
        if (task is null)
        {
            return TaskApiError(
                $"No repository task named '{taskId}' exists in '{parentRequest.Task.RepositoryRootPath}'.");
        }
        if (arguments["input"] is not null and not JsonObject)
        {
            return TaskApiError("Task input must be a JSON object.");
        }
        if (!RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
                task.InputSchema,
                arguments["input"] as JsonObject,
                out var input,
                out var errorMessage))
        {
            return TaskApiError(errorMessage);
        }

        var result = await ExecuteCoreAsync(
                task,
                parentRequest.SessionId,
                input,
                correlationId,
                parentRequest.TaskCallChain.Append(taskId).ToArray(),
                parentRequest.CaptureTrace,
                cancellationToken, parentRequest.OperationContext, parentRequest.SecretValues)
            .ConfigureAwait(false);
        // Trace payloads are retained separately so tracing cannot grow the JavaScript
        // response past its runtime limit or change a composed task's behavior.
        return new RepositoryTaskApiResult(
            RequestResult.ToolResult(
                RepositoryTaskProtocol.BuildRunResult(result, includeCallTrace: false),
                isError: result.Status != RepositoryTaskRunStatus.Passed),
            parentRequest.CaptureTrace ? result.ToolCalls : null,
            parentRequest.CaptureTrace ? result.SourceTrace : null,
            parentRequest.CaptureTrace ? result.Assertions.Select(RepositoryTaskCallTrace.CaptureAssertion).ToArray() : null);
    }

    private static string? NormalizeOptionalString(JsonNode? value)
    {
        if (value is not JsonValue jsonValue
            || !jsonValue.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Trim();
    }

    private static int ReadMaximumResults(JsonNode? value)
    {
        if (value is null)
        {
            return RepositoryTaskProtocol.DefaultMaximumDiscoveryResults;
        }
        if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out var maximumResults))
        {
            return RepositoryTaskProtocol.DefaultMaximumDiscoveryResults;
        }

        return Math.Clamp(maximumResults, 1, RepositoryTaskProtocol.MaximumDiscoveryResults);
    }

    private static RepositoryTaskApiResult TaskApiError(string message)
        => new(RequestResult.ToolResult(
            new JsonObject { ["message"] = message },
            isError: true));
}
