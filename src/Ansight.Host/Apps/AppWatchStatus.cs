namespace Ansight.Host.Apps;

public sealed record AppWatchStatus(
    AppWatchDefinition Watch, string State, string? SessionId = null, string? LastSessionId = null,
    string? ProcessIdentity = null, string? Message = null)
{
    public IReadOnlyList<AppWatchDeviceStatus> Devices { get; init; } = [];
}
