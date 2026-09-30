using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Sessions;

public enum RuntimeSessionTransferKind
{
    Telemetry,
    Log,
    AppEvent,
    AppProfile,
    Screenshot,
    VisualTree,
    TouchInput,
    AnnotatedFeedback
}
