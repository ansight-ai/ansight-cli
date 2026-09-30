using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Apps;

public sealed record RuntimeClientAppStateChangedEvent(
    DateTimeOffset OccurredAtUtc,
    string SessionId,
    string AppId,
    string ClientName,
    AppLifecycleState PreviousState,
    AppLifecycleState CurrentState,
    DateTimeOffset? ChangedAtUtc) : RuntimeEvent(OccurredAtUtc);
