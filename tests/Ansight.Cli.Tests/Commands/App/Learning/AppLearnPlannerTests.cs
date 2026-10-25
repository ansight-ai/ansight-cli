using Ansight.Cli.Commands.App.Learning;
using System.Text.Json.Nodes;

namespace Ansight.Cli.Tests.Commands.App.Learning;

public sealed class AppLearnPlannerTests
{
    [Fact]
    public void RanksSharedSetupAndMultiStepRoutesWithinBudget()
    {
        var graph = AppLearnTestData.Definition();
        var plan = AppLearnPlanner.Build(graph, AppLearnTestData.Evidence(graph), new HashSet<string>(), 3);
        Assert.Equal(3, plan.Candidates.Count);
        Assert.Equal("Catalog", plan.Candidates[0].To);
        Assert.Equal(1, plan.Candidates[0].StepCount);
        Assert.Equal("Home", plan.Candidates[0].From);
        Assert.All(plan.Candidates.Skip(1), candidate => Assert.Equal(2, candidate.StepCount));
        Assert.DoesNotContain(plan.Candidates, candidate => candidate.To == "Home");
        Assert.Contains("shared setup for 2", plan.Candidates[0].Reason);
    }

    [Fact]
    public void ExcludesUnsupportedBindingsAndRoutesThroughThem()
    {
        var graph = AppLearnTestData.Definition();
        var evidence = AppLearnTestData.Evidence(graph);
        evidence["bindingCandidates"]![0]!["mechanism"] = "app_tool";
        var plan = AppLearnPlanner.Build(graph, evidence, new HashSet<string>(), 10);
        Assert.Equal("Map", Assert.Single(plan.Candidates).To);
        Assert.Contains(plan.Diagnostics, value => value.Contains("open-catalog"));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("swipe")]
    public void ExcludesNonTapBindings(string action)
    {
        var graph = AppLearnTestData.Definition();
        var evidence = AppLearnTestData.Evidence(graph);
        foreach (var binding in evidence["bindingCandidates"]!.AsArray()) binding!["configuration"]!["action"] = action;
        Assert.Empty(AppLearnPlanner.Build(graph, evidence, new HashSet<string>(), 10).Candidates);
    }

    [Fact]
    public void DoesNotInventParametersOrSelectors()
    {
        var graph = AppLearnTestData.Definition();
        graph["edges"]![0]!["parameters"] = new JsonArray("query");
        var evidence = AppLearnTestData.Evidence(graph);
        evidence["bindingCandidates"]![4]!["configuration"]!["selector"]!["automationId"] = "invented";
        var plan = AppLearnPlanner.Build(graph, evidence, new HashSet<string>(), 10);
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Diagnostics, value => value.Contains("No new reusable"));
    }

    [Fact]
    public void StableIdentitySurvivesGraphIdChangesAndSkipsExistingTasks()
    {
        var graph = AppLearnTestData.Definition();
        var first = AppLearnPlanner.Build(graph, AppLearnTestData.Evidence(graph), new HashSet<string>(), 10);
        foreach (var node in graph["nodes"]!.AsArray()) node!["id"] = "new-" + node["id"]!.GetValue<string>();
        foreach (var edge in graph["edges"]!.AsArray())
        {
            foreach (var property in new[] { "id", "from", "to" }) edge![property] = "new-" + edge[property]!.GetValue<string>();
        }
        var next = AppLearnPlanner.Build(graph, AppLearnTestData.Evidence(graph), new HashSet<string>(), 10);
        Assert.Equal(first.Candidates.Select(candidate => candidate.TaskId), next.Candidates.Select(candidate => candidate.TaskId));
        var repeated = AppLearnPlanner.Build(graph, AppLearnTestData.Evidence(graph),
            first.Candidates.Select(candidate => candidate.TaskId).ToHashSet(), 10);
        Assert.Empty(repeated.Candidates);
        Assert.Contains(repeated.Diagnostics, value => value.StartsWith("Preserved existing task"));
    }

    [Fact]
    public void DoesNotModifyExplorationEvidence()
    {
        var graph = AppLearnTestData.Definition();
        var evidence = AppLearnTestData.Evidence(graph);
        var original = graph.ToJsonString();
        var plan = AppLearnPlanner.Build(graph, evidence, new HashSet<string>(), 2);
        plan.Candidates[0].Route["startingDestination"]!["name"] = "Changed";
        Assert.Equal(original, graph.ToJsonString());
    }

    [Fact]
    public void OversizedEvidenceIsReportedInsteadOfTruncatedOrSentToAuthor()
    {
        var graph = AppLearnTestData.Definition();
        graph["nodes"]![0]!["purpose"] = new string('x', 8_000);
        var plan = AppLearnPlanner.Build(graph, AppLearnTestData.Evidence(graph), new HashSet<string>(), 3);
        Assert.Empty(plan.Candidates);
        Assert.Contains(plan.Diagnostics, message => message.Contains("handoff limit"));
    }
}
