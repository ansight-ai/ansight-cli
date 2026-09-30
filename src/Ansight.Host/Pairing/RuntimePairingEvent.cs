using AppLifecycleState = global::Ansight.AppLifecycleState;

namespace Ansight.Host.Pairing;

public sealed record RuntimePairingEvent(
    DateTimeOffset OccurredAtUtc,
    RuntimePairingEventKind Kind,
    string SessionId,
    string AppId,
    string ClientName,
    string RemoteAddress,
    string? ConfigId,
    string? ReasonCode,
    string? ReasonMessage,
    bool IsFirstConnection = false) : RuntimeEvent(OccurredAtUtc);
