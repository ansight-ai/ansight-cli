using Ansight.Host;

namespace Ansight.Cli.Commands.Repository;

internal sealed record RepositoryAutomationRunsOutput(
    string Schema,
    string AppId,
    IReadOnlyList<AutomationRunCompletedEvent> Runs);
