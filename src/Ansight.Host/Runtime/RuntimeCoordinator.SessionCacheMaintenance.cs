namespace Ansight.Host.Runtime;

using Ansight.Host.Sessions;

public sealed partial class RuntimeCoordinator
{
    private static readonly TimeSpan SessionCacheMaintenanceInterval = TimeSpan.FromMinutes(15);
    private SessionCacheMaintenanceResult? lastSessionCacheCleanup;

    public event EventHandler<SessionCacheMaintenanceResult>? SessionCacheMaintenanceCompleted;

    public SessionCacheMaintenanceResult? LastSessionCacheCleanup => Volatile.Read(ref lastSessionCacheCleanup);

    private async Task RunSessionCacheMaintenanceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = SessionCacheMaintenance.RunOnce(this, cancellationToken);
                if (result.DeletedSessionCount > 0)
                {
                    Volatile.Write(ref lastSessionCacheCleanup, result);
                    log.Info($"session_cache_auto_cleanup deletedSessions={result.DeletedSessionCount} cacheBytes={result.CacheSizeBytes}");
                }

                SessionCacheMaintenanceCompleted?.Invoke(this, result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.Exception(exception);
            }

            try
            {
                await Task.Delay(SessionCacheMaintenanceInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
