using Ansight.Host;

namespace Ansight.Cli.Commands.Repository;

internal sealed record RepositoryAutomationListOutput(
    string Schema,
    IReadOnlyList<RepositoryAutomationTrigger> Triggers);
