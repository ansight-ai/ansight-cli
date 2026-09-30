namespace Ansight.RemoteSimulator.Core.Runtime;

public sealed record RemoteRuntimeSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<RemoteRuntimeDevice> Devices,
    string? Error)
{
    public static RemoteRuntimeSnapshot Empty { get; } =
        new(DateTimeOffset.MinValue, Array.Empty<RemoteRuntimeDevice>(), null);
}
