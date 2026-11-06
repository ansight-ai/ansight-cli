using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Tasks;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

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
