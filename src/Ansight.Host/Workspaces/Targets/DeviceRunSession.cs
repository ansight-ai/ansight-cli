namespace Ansight.Host.Workspaces.Targets;

internal sealed class DeviceRunSession(
    AppSessionSnapshot session,
    IRuntimeState state,
    IExternalSessionScreenshotCaptureManager captures,
    IDisposable? deviceClaim,
    DeviceSessionEvidence? evidence = null,
    TimeSpan observationTail = default,
    Func<string, CancellationToken, Task>? drainTriggers = null,
    Func<Task>? releaseResources = null,
    Func<Task>? stopAdditionalEvidence = null) : IAsyncDisposable
{
    private int disposed;
    public AppSessionSnapshot Session { get; } = session;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var completed = false;
        try
        {
            if (stopAdditionalEvidence is not null) await stopAdditionalEvidence().ConfigureAwait(false);
            // Revoke input immediately, even if stopping capture fails.
            HostSessionEvents.Publish(state, Session.SessionId, "run.completed", "host.run.completed");
            state.EndDeviceSession(Session.SessionId);
            try
            {
                try
                {
                    if (observationTail > TimeSpan.Zero) await Task.Delay(observationTail).ConfigureAwait(false);
                    if (drainTriggers is not null)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        try { await drainTriggers(Session.SessionId, deadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { state.AddSessionLog(Session.SessionId, "Final trigger evidence exceeded the 30-second drain budget."); }
                    }
                }
                finally
                {
                    if (evidence is not null) await evidence.StopAsync(Session.SessionId).ConfigureAwait(false);
                }
            }
            finally
            {
                await captures.StopAsync(Session.SessionId, "Device execution completed.").ConfigureAwait(false);
            }
            completed = true;
        }
        finally
        {
            try
            {
                state.PublishRuntimeEvent(new RuntimeSessionCaptureEvent(DateTimeOffset.UtcNow,
                    RuntimeSessionCaptureEventKind.Finalized, Session.SessionId, Session.AppId, Session.ClientName,
                    completed ? "Completed" : "Failed", completed ? "Host device evidence drained." : "Host device capture failed while draining evidence."));
            }
            finally
            {
                try { deviceClaim?.Dispose(); }
                finally { if (releaseResources is not null) await releaseResources().ConfigureAwait(false); }
            }
        }
    }
}
