namespace Ansight.MacSimulatorHid;

public sealed record MacSimulatorHidCompatibility(
    bool IsAvailable,
    string Status,
    string Message,
    string? DeveloperDirectory);
