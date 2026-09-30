using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestBatchOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    string WorkspacePath,
    string ResultFilePath,
    int ExitCode,
    WorkspaceTestBatchResult Result)
{
    public IReadOnlyList<TestSessionLink> Sessions => Result.Results
        .SelectMany(result => TestSessionLink.From(result, result.Test?.TestId))
        .ToArray();
}
