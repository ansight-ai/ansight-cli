using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Tools;

internal static class AgentWorkspaceToolPolicy
{
    private static bool IsWorkspaceTool(string name) => name is
        "ansight_list_tasks" or "ansight_describe_module" or "ansight_run_task"
        or "ansight_run_task_batch" or "ansight_declare_uncovered_step"
        || name.StartsWith("ansight_task_", StringComparison.Ordinal);

    internal static void RemoveWorkspaceTools(JsonArray tools)
    {
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index]?["name"]?.GetValue<string>() is { } name && IsWorkspaceTool(name))
            {
                tools.RemoveAt(index);
            }
        }
    }

    internal static void EnsureAllowed(string name, bool enabled)
    {
        if (!enabled && IsWorkspaceTool(name))
        {
            throw new InvalidOperationException("Workspace tools are disabled for this agent run.");
        }
    }
}
