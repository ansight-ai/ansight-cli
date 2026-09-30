namespace Ansight.Host.Runtime.SessionCaptureStorage;



internal readonly record struct SessionLogPersistenceKey(
    string SessionId,
    string StreamId);
