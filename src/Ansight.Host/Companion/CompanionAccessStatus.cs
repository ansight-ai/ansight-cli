namespace Ansight.Host.Companion;

public sealed record CompanionAccessStatus(
    CompanionAccessMode Mode,
    bool IsEnabled,
    bool IsAvailable,
    string Status,
    int ConnectionCount);
