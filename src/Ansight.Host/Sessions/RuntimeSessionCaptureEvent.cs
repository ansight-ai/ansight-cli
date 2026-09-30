using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Sessions;

public sealed record RuntimeSessionCaptureEvent(
    DateTimeOffset OccurredAtUtc,
    RuntimeSessionCaptureEventKind Kind,
    string SessionId,
    string AppId,
    string ClientName,
    string Status,
    string? Message) : RuntimeEvent(OccurredAtUtc);
