using Ansight.Host;

namespace Ansight.Cli.Commands.Session;

internal sealed record SessionListPage(
    int MatchedCount,
    int Limit,
    bool IsTruncated,
    string? NextCursor,
    IReadOnlyList<SessionListPageItem> Items);
