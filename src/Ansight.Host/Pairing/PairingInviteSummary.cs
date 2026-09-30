namespace Ansight.Host.Pairing;

public sealed record PairingInviteSummary(
    string InviteId,
    string Scope,
    string AppId,
    string AppName,
    string Schema,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsConsumed,
    bool IsExpired,
    string Status,
    string? SourceKind,
    bool IsReadOnly);
