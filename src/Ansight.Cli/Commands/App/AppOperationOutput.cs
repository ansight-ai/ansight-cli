using Ansight.Host;

namespace Ansight.Cli.Commands.App;

internal sealed record AppOperationOutput(
    string Schema,
    string Operation,
    AppOperationResult Result);
