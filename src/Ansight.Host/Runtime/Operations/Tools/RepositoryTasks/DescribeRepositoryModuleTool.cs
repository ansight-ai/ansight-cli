using System.Text.Json.Nodes;
using Ansight.Host.Runtime.Automation;
using Ansight.Host.Runtime.RepositoryContracts;
using Ansight.Tools;

namespace Ansight.Host.Runtime.Operations.Tools.RepositoryTasks;

internal sealed class DescribeRepositoryModuleTool : RepositoryTaskTool
{
    private static readonly TimeSpan defaultFunctionTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan defaultActionTimeout = TimeSpan.FromSeconds(30);
    public DescribeRepositoryModuleTool(OperationServices services)
        : base(services)
    {
    }

    public override string Name => "ansight_describe_module";

    protected override string Title => "Describe Repository Module";

    protected override string Description =>
        "Extract one exact repository task or trigger contract, including its JSON schemas and runtime API.";

    protected override JsonObject InputSchema => ToolSchema.Object(
        properties: new Dictionary<string, ToolSchema>
        {
            ["sessionId"] = ToolSchema.String(
                "Optional live Ansight session used to locate the connected app repository.",
                nullable: true),
            ["appId"] = ToolSchema.String(
                "App id whose connected Agent workspace contains the module.",
                nullable: true),
            ["moduleType"] = ToolSchema.String(
                "Repository module kind.",
                enumValues: ["task", "trigger"]),
            ["moduleId"] = ToolSchema.String(
                "Exact repository-relative module id without its file extension."),
            ["includeDefinitions"] = ToolSchema.Boolean(
                "Include the complete definition JSON Schema and TypeScript runtime declarations. Defaults to true.",
                nullable: true)
        },
        required: ["moduleType", "moduleId"],
        additionalProperties: false).ToJson();

    public override Task<RequestResult> ExecuteAsync(JsonObject? arguments, string? correlationId)
    {
        if (!TryResolveRepository(
                arguments,
                requireLiveSession: false,
                out _,
                out var app,
                out var taskLoadResult,
                out var errorMessage))
        {
            return Task.FromResult(ToolError(errorMessage));
        }

        var moduleType = NormalizeOptionalString(arguments?["moduleType"]?.GetValue<string>());
        var moduleId = NormalizeOptionalString(arguments?["moduleId"]?.GetValue<string>());
        if (moduleId is null)
        {
            return Task.FromResult(ToolError("moduleId is required."));
        }

        var includeDefinitions = arguments?["includeDefinitions"]?.GetValue<bool>() ?? true;
        var warnings = new List<string>(taskLoadResult!.Warnings);
        JsonObject description;
        if (string.Equals(moduleType, "task", StringComparison.Ordinal))
        {
            var task = taskLoadResult.Tasks.FirstOrDefault(candidate => string.Equals(
                candidate.TaskId,
                moduleId,
                StringComparison.Ordinal));
            if (task is null)
            {
                return Task.FromResult(ToolError($"No connected repository task named '{moduleId}' exists for app '{app!.AppId}'."));
            }

            description = RepositoryModuleContractBuilder.BuildTask(task, includeDefinitions);
        }
        else if (string.Equals(moduleType, "trigger", StringComparison.Ordinal))
        {
            var triggerLoadResult = RepositoryAutomationTriggerLoader.Load(
                [app!.CodebasePath!],
                defaultFunctionTimeout,
                defaultActionTimeout,
                app.AppId);
            warnings.AddRange(triggerLoadResult.Warnings);
            var trigger = triggerLoadResult.Catalog.Triggers.FirstOrDefault(candidate => string.Equals(
                candidate.TriggerId,
                moduleId,
                StringComparison.Ordinal));
            if (trigger is null)
            {
                return Task.FromResult(ToolError($"No connected repository trigger named '{moduleId}' exists for app '{app.AppId}'."));
            }

            description = RepositoryModuleContractBuilder.BuildTrigger(trigger, includeDefinitions);
        }
        else
        {
            return Task.FromResult(ToolError("moduleType must be 'task' or 'trigger'."));
        }

        description["appId"] = app!.AppId;
        description["warnings"] = new JsonArray(
            warnings.Distinct(StringComparer.Ordinal).Select(warning => (JsonNode?)warning).ToArray());
        return Task.FromResult(RequestResult.ToolResult(description, isError: false));
    }
}
