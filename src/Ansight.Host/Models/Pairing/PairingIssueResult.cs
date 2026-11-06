namespace Ansight.Host.Models.Pairing;

public sealed record PairingIssueResult(string ConfigId, string DesktopPath, DateTimeOffset ExpiresAt);
