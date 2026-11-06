namespace Ansight.Host.Apps;

internal sealed record AppWatchObservation(DeviceDescriptor Device, string? ProcessIdentity, string? Error = null,
    global::Ansight.AppLifecycleState AppState = global::Ansight.AppLifecycleState.Unknown,
    DateTimeOffset? ObservedAtUtc = null);
