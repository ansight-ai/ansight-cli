namespace Ansight.Host.Sessions;

public sealed class SessionQueryService
{
    private readonly ISessionReader sessionReader;

    internal SessionQueryService(ISessionReader sessionReader)
    {
        this.sessionReader = sessionReader ?? throw new ArgumentNullException(nameof(sessionReader));
    }

    public IReadOnlyList<AppSessionSnapshot> GetSummaries()
        => sessionReader.GetSessionSummaries();

    public bool TryGetSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
        => sessionReader.TryGetSessionSnapshot(sessionId, out snapshot);

    public bool TryGetLiveContentSnapshot(string sessionId, out AppSessionSnapshot? snapshot)
        => sessionReader.TryGetSessionLiveContentSnapshot(sessionId, out snapshot);

    public bool TryGetReplaySnapshot(string sessionId, out AppSessionSnapshot? snapshot)
        => sessionReader.TryGetSessionReplaySnapshot(sessionId, out snapshot);

    public bool TryGetLiveUpdate(
        string sessionId,
        SessionLiveUpdateCursor cursor,
        out SessionLiveUpdate? update)
        => sessionReader.TryGetSessionLiveUpdate(sessionId, cursor, out update);

    public Task<AppSessionSnapshot?> LoadSnapshotAsync(
        string sessionId,
        IProgress<SessionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => sessionReader.LoadSessionSnapshotAsync(sessionId, progress, cancellationToken);
}
