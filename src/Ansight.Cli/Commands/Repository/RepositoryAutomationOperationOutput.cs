namespace Ansight.Cli.Commands.Repository;

internal sealed record RepositoryAutomationOperationOutput(
    string Schema,
    string Operation,
    string AppId,
    bool IsSuccess);
