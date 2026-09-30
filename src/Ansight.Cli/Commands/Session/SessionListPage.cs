using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionListPage(
    int MatchedCount,
    int Limit,
    bool IsTruncated,
    string? NextCursor,
    IReadOnlyList<SessionListPageItem> Items);

internal sealed record SessionListPageItem(
    AppSessionSnapshot Session,
    bool IsConnected);

internal sealed record SessionListCursor(
    DateTimeOffset LastUpdatedUtc,
    string SessionId);
