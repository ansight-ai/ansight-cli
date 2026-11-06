namespace Ansight.SimCtl;

public sealed record SimCtlDevice(
    string Udid,
    string Name,
    string State,
    bool IsAvailable,
    string RuntimeIdentifier,
    DateTimeOffset? LastBootedUtc,
    string DataPath = "")
{
    public bool IsBooted => IsAvailable && string.Equals(State, "Booted", StringComparison.OrdinalIgnoreCase);
}
