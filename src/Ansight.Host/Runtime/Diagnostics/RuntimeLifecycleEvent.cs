using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Runtime.Diagnostics;

public sealed record RuntimeLifecycleEvent(
    DateTimeOffset OccurredAtUtc,
    RuntimeLifecycleEventKind Kind,
    string Message) : RuntimeEvent(OccurredAtUtc);
