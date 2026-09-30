using Ansight.Host;

namespace Ansight.Cli.Commands.Tasks;

internal sealed record RepositoryTaskRunOutput(
    string Schema,
    RepositoryTaskRunResult Result);
