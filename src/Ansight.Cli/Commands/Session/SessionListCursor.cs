using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionListCursor(
    DateTimeOffset LastUpdatedUtc,
    string SessionId);
