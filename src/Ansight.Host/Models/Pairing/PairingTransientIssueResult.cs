namespace Ansight.Host.Models.Pairing;

public sealed record PairingTransientIssueResult(string ConfigId, DateTimeOffset ExpiresAt);
