namespace Ansight.Host.Models.Pairing;

public sealed record PairingIssueResult(string ConfigId, string DesktopPath, DateTimeOffset ExpiresAt);

public sealed record PairingTransientIssueResult(string ConfigId, DateTimeOffset ExpiresAt);
