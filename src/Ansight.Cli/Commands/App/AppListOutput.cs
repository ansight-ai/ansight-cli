using Ansight.Host;

namespace Ansight.Cli.Commands.App;

internal sealed record AppListOutput(
    string Schema,
    IReadOnlyList<AppDescriptor> Apps);
