using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Diagnostics;

public abstract record RuntimeEvent(DateTimeOffset OccurredAtUtc);
