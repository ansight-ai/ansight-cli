using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Sessions;

public enum RuntimeSessionCaptureEventKind
{
    Started,
    Updated,
    Stopped,
    Finalized
}
