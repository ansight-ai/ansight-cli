namespace Ansight.Host.Runtime.State;

using AppLifecycleState = global::Ansight.AppLifecycleState;
using Ansight.Host;

internal interface ISessionLifecycle
{
    string CreateDeviceSession(WorkspaceTestTarget target);
    bool IsDeviceSessionActive(string sessionId);
    IReadOnlyList<string> GetActiveDeviceSessionIds();
    void EndDeviceSession(string sessionId);

    string ReserveSessionId(string appId, string? processSessionId);

    string CreateSession(
        string appId,
        string clientName,
        IPAddress remoteAddress,
        string? configId,
        string? processSessionId,
        string? reservedSessionId = null);

    int BeginSessionConnection(string sessionId);
    void SetSessionStatus(string sessionId, string status, string? message = null);
    void SetSessionAppState(string sessionId, AppLifecycleState state, DateTimeOffset? changedAtUtc = null);

    SessionImportResult ImportSessionSnapshot(
        AppSessionSnapshot snapshot,
        IReadOnlyDictionary<string, byte[]> imageBytesByFrameId,
        byte[]? appIconBytes = null,
        IReadOnlyDictionary<string, byte[]>? artifactBytesByRelativePath = null,
        SessionReplaySource? replaySource = null);
}
