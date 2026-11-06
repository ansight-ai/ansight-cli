namespace Ansight.Host.Apps;

internal sealed record AppWatchDiscovery(IReadOnlyList<AppWatchObservation> Observations, string? Warning = null);
