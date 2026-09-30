namespace Ansight.Cli.Commands.Session;

internal sealed record SessionListOutput(
    string Schema,
    int MatchedCount,
    int ReturnedCount,
    int Limit,
    bool IsTruncated,
    string? NextCursor,
    IReadOnlyList<SessionSummaryOutput> Sessions);
