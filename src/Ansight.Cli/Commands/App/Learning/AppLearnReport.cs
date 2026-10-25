namespace Ansight.Cli.Commands.App.Learning;

internal sealed record AppLearnReport(
    string Schema,
    string AppId,
    string WorkspacePath,
    string ReportPath,
    bool DraftOnly,
    string Status,
    AppGraphExploreOutput Exploration,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<AppLearnTaskResult> Tasks);
