using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.Test;

internal sealed record TestRunOutput(
    string Schema,
    DateTimeOffset CompletedUtc,
    string WorkspacePath,
    string TestId,
    string ResultFilePath,
    int ExitCode,
    WorkspaceTestRunResult Result)
{
    public IReadOnlyList<TestSessionLink> Sessions => TestSessionLink.From(Result, TestId);
}
