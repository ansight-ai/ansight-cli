using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionDetailOutput(
    string Schema,
    AppSessionSnapshot Session,
    bool IsConnected);
