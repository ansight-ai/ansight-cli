using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal abstract class RepositoryTaskTool : Operation
{
    protected RepositoryTaskTool(OperationServices services)
        : base(services)
    {
    }

    protected bool TryResolveRepository(
        JsonObject? arguments,
        bool requireLiveSession,
        out AppSessionSnapshot? session,
        out KnownAppDefinition? app,
        out RepositoryTaskLoadResult? loadResult,
        out string errorMessage)
    {
        session = null;
        app = null;
        loadResult = null;
        errorMessage = string.Empty;
        var sessionId = NormalizeOptionalString(arguments?["sessionId"]?.GetValue<string>());
        var appId = NormalizeOptionalString(arguments?["appId"]?.GetValue<string>());
        if (requireLiveSession)
        {
            if (!sessionResolver.TryResolveLiveSession(arguments, out session, out errorMessage))
            {
                return false;
            }

            appId = session!.AppId;
        }
        else if (sessionId is not null)
        {
            if (!runtimeState.TryGetSessionSnapshot(sessionId, out session) || session is null)
            {
                errorMessage = $"No Ansight session named '{sessionId}' exists.";
                return false;
            }

            if (appId is not null && !string.Equals(appId, session.AppId, StringComparison.Ordinal))
            {
                errorMessage = $"Session '{sessionId}' belongs to app '{session.AppId}', not '{appId}'.";
                return false;
            }

            appId = session.AppId;
        }

        if (appId is null)
        {
            errorMessage = "Provide appId or sessionId to resolve repository tasks.";
            return false;
        }

        if (!knownAppStore.TryGet(appId, out app) || app is null)
        {
            errorMessage = $"App '{appId}' is not registered in Ansight.";
            return false;
        }

        if (!app.RepositoryAutomationsEnabled)
        {
            errorMessage =
                $"Repository automations are not connected for app '{appId}'. Register its trusted repository with ansight app register <app-id> --codebase <path> first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(app.CodebasePath))
        {
            errorMessage = $"App '{appId}' does not have a linked repository. Register it with ansight app register <app-id> --codebase <path>.";
            return false;
        }

        loadResult = repositoryTaskRouter.Load(RepositoryTaskWorkspaceScope.Resolve(appId, app.CodebasePath), appId);
        return true;
    }

}

internal sealed class ListRepositoryTasksTool : RepositoryTaskTool
{
    public ListRepositoryTasksTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_list_tasks";

    protected override string Title => "List Repository Tasks";

    protected override string Description =>
        "Search trusted named tasks from the selected app repository's ansight/tasks directory by focused keywords or feature.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Optional selected Ansight session.", nullable: true),
            ["appId"] = ToolSchema.String("Optional app id when no session is supplied.", nullable: true),
            ["query"] = ToolSchema.String(
                "Focused intent words ranked against task id, title, description, feature, keywords, and declared input schema values. Behavioral synonyms and conservative Levenshtein typo matching are applied; concrete runtime values may remain unmatched.",
                nullable: true),
            ["feature"] = ToolSchema.String("Optional feature or domain filter.", nullable: true),
            ["maxResults"] = ToolSchema.Integer("Maximum matching tasks. Defaults to 10 and is capped at 20.", nullable: true),
            ["includeDiagnostics"] = ToolSchema.Boolean("Include bounded task inventory and discovery decisions for tracing.", nullable: true)
        },
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryResolveRepository(
                arguments,
                requireLiveSession: false,
                out var session,
                out var app,
                out var loadResult,
                out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage));
        }

        var query = NormalizeOptionalString(arguments?["query"]?.GetValue<string>());
        var feature = NormalizeOptionalString(arguments?["feature"]?.GetValue<string>());
        var maxResults = Math.Clamp(
            arguments?["maxResults"]?.GetValue<int>() ?? RepositoryTaskProtocol.DefaultMaximumDiscoveryResults,
            1,
            RepositoryTaskProtocol.MaximumDiscoveryResults);
        return Task.FromResult(RequestResult.ToolResult(
            RepositoryTaskProtocol.BuildDiscoveryResult(
                loadResult!,
                app!.AppId,
                session?.SessionId ?? string.Empty,
                query,
                feature,
                maxResults,
                includeDiagnostics: arguments?["includeDiagnostics"]?.GetValue<bool>() == true),
            isError: false));
    }
}

internal sealed class RunRepositoryTaskTool : RepositoryTaskTool
{
    public RunRepositoryTaskTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_run_task";

    protected override string Title => "Run Repository Task";

    protected override string Description =>
        "Run one trusted named ansight/tasks script against the host-enforced live session and return its deterministic assertions.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String("Specific live session to target.", nullable: true),
            ["appId"] = ToolSchema.String("App id to target when exactly one live session exists.", nullable: true),
            ["taskId"] = ToolSchema.String("Exact task id returned by ansight_list_tasks."),
            ["input"] = ToolSchema.Object(
                description: "Task parameters validated against the task's declared inputSchema.",
                properties: new Dictionary<string, ToolSchema>(),
                additionalProperties: true,
                nullable: true)
        },
        required: ["taskId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
        => ExecuteAsync(arguments, correlationId, null);

    public override async Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId, OperationExecutionContext? context)
    {
        if (!TryResolveRepository(
                arguments,
                requireLiveSession: true,
                out var session,
                out _,
                out var loadResult,
                out var errorMessage))
        {
            return ToolError(errorMessage);
        }

        var taskId = NormalizeOptionalString(arguments?["taskId"]?.GetValue<string>());
        if (taskId is null)
        {
            return ToolError("taskId is required.");
        }

        var task = loadResult!.Tasks.FirstOrDefault(candidate => string.Equals(
            candidate.TaskId,
            taskId,
            StringComparison.Ordinal));
        if (task is null)
        {
            return ToolError($"No connected repository task named '{taskId}' exists for app '{session!.AppId}'.");
        }

        if (!RepositoryTaskInputValidator.TryValidateAndApplyDefaults(
                task.InputSchema,
                arguments?["input"] as JsonObject,
                out var input,
                out errorMessage))
        {
            return ToolError(errorMessage);
        }

        var result = await repositoryTaskRouter.ExecuteAsync(
                task,
                session!.SessionId,
                input,
                correlationId,
                CancellationToken.None, context)
            .ConfigureAwait(false);
        var payload = RepositoryTaskProtocol.BuildRunResult(result);
        return RequestResult.ToolResult(
            payload,
            isError: result.Status != RepositoryTaskRunStatus.Passed);
    }
}
