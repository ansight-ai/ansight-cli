namespace Ansight.Host.Apps;

internal interface IAppWatchBackend
{
    Task<AppWatchDiscovery> ObserveAsync(AppWatchDefinition watch, CancellationToken cancellationToken);

    Task<IAppWatchCapture?> TryStartAsync(AppWatchDefinition watch, AppWatchObservation observation,
        CancellationToken cancellationToken);
}
