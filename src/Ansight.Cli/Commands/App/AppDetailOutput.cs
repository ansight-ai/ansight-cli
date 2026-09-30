using Ansight.Host;

namespace Ansight.Cli.Commands.App;

internal sealed record AppDetailOutput(
    string Schema,
    string AppId,
    bool IsFound,
    AppDescriptor? App);
