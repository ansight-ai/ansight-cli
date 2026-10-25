namespace Ansight.Cli.Commands.App.Learning;

internal sealed record AppLearnPlan(
    IReadOnlyList<AppLearnCandidate> Candidates,
    IReadOnlyList<string> Diagnostics);
