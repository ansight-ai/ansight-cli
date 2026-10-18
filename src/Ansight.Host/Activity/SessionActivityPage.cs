namespace Ansight.Host.Activity;

public sealed record SessionActivityPage(
    IReadOnlyList<SessionActivityRecord> Records,
    string? NextCursor,
    bool RetentionLimited,
    bool CursorExpired,
    int SkippedRecords,
    bool IncompleteTail,
    string? PersistenceError,
    bool ImportedHistoryIncomplete = false);
