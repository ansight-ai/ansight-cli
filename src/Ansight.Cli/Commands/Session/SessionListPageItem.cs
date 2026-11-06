using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionListPageItem(
    AppSessionSnapshot Session,
    bool IsConnected);
