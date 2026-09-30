namespace Ansight.Cli.AppGraphs;

internal sealed record AppGraphValidation(
    IReadOnlyList<string> Errors,
    int EdgeCount,
    int BindingCount);
