namespace Ansight.Cli.AppGraphs;

internal sealed record AppGraphPlan(
    IReadOnlyList<string> Errors,
    IReadOnlyList<AppGraphPlanStep> Steps);
