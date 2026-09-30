namespace Ansight.Host.Runtime.State;

using Ansight.Host;

internal interface ISessionReader
{
    IReadOnlyList<AppSessionSnapshot> GetSessionSummaries();
    bool TryGetSessionContext(string sessionId, out RuntimeSessionContext? context);
    bool TryGetSessionSnapshot(string sessionId, out AppSessionSnapshot? snapshot);
    bool TryGetSessionLiveContentSnapshot(string sessionId, out AppSessionSnapshot? snapshot);
    bool TryGetSessionReplaySnapshot(string sessionId, out AppSessionSnapshot? snapshot);
    bool TryGetSessionLiveUpdate(
        string sessionId,
        SessionLiveUpdateCursor cursor,
        out SessionLiveUpdate? update);
    bool TryGetSessionSnapshotForExport(string sessionId, out AppSessionSnapshot? snapshot);

    Task<AppSessionSnapshot?> LoadSessionSnapshotAsync(
        string sessionId,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    bool TryGetSessionCacheSizeBytes(string sessionId, out long cacheSizeBytes);
    bool ExpandSessionCache(string sessionId);
    int CompactSessionCache(
        int compactionAgeDays,
        DateTimeOffset nowUtc,
        ISet<string>? protectedSessionIds = null);
    IReadOnlyList<RuntimeFocusedControlSnapshot> GetFocusedControlSnapshots();
    void SetFocusedControl(RuntimeFocusedControlSnapshot snapshot);
    void ClearFocusedControl(string? sessionId = null);
}
