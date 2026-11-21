using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent.Tools;

namespace Ansight.Host.Tests.Unit.SimulatorAgent;

public sealed class AgentWorkspaceToolPolicyTests
{
    [Theory]
    [InlineData("ansight_list_tasks")]
    [InlineData("ansight_describe_module")]
    [InlineData("ansight_run_task")]
    [InlineData("ansight_run_task_batch")]
    [InlineData("ansight_task_example_123")]
    [InlineData("ansight_declare_uncovered_step")]
    public void DisabledWorkspaceToolsAreNotExposedAndCannotExecute(string name)
    {
        var tools = new JsonArray(new JsonObject { ["name"] = name }, new JsonObject { ["name"] = "ansight_tap_ui" });
        AgentWorkspaceToolPolicy.RemoveWorkspaceTools(tools);
        Assert.Equal("ansight_tap_ui", Assert.Single(tools)!["name"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => AgentWorkspaceToolPolicy.EnsureAllowed(name, false));
        AgentWorkspaceToolPolicy.EnsureAllowed(name, true);
        AgentWorkspaceToolPolicy.EnsureAllowed("ansight_tap_ui", false);
        Assert.Null(AgentTaskBatch.BuildDefinition(tools));
    }
}
