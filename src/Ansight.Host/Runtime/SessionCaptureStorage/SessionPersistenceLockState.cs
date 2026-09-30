namespace Ansight.Host.Runtime.SessionCaptureStorage;

internal sealed class SessionPersistenceLockState
{
    public object Gate { get; } = new();

    public int ReferenceCount { get; set; }
}
