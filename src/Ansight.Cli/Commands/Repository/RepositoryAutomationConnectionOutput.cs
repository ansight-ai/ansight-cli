using Ansight.Host;

namespace Ansight.Cli.Commands.Repository;

internal sealed record RepositoryAutomationConnectionOutput(
    string Schema,
    string Operation,
    RepositoryAutomationConnection Connection);
