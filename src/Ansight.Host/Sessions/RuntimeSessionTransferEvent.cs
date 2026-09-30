using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Sessions;

public sealed record RuntimeSessionTransferEvent(
    DateTimeOffset OccurredAtUtc,
    RuntimeSessionTransferKind Kind,
    string SessionId,
    string AppId,
    string ClientName,
    int ItemCount,
    string Message,
    DateTimeOffset? CapturedAtUtc) : RuntimeEvent(OccurredAtUtc);
