namespace Ansight.Host.Workspaces.Execution;

/// <summary>Owns AI preparation while the app launches, including abandoned metered runs.</summary>
internal sealed class WorkspaceRunPreparationScope(
    IWorkspaceTestRunGateway? gateway,
    CancellationToken cancellationToken,
    Action<string> reportWarning) : IAsyncDisposable
{
    private readonly CancellationTokenSource preparationCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private Task<WorkspaceTestRunPreparation>? preparationTask;
    private bool completionHandled;
    private bool disposed;

    public Task<WorkspaceTestRunPreparation> PrepareAsync(
        Func<CancellationToken, Task<WorkspaceTestRunPreparation>> prepare)
        => preparationTask ??= PrepareCoreAsync(prepare);

    // The caller owns normal completion and its audit/cost persistence.
    public void MarkCompletionHandled() => completionHandled = true;

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (preparationTask is null || completionHandled) return;

            // On launch failure, cancel outstanding network work and observe its cleanup.
            await preparationCancellation.CancelAsync().ConfigureAwait(false);
            var preparation = await preparationTask.ConfigureAwait(false);
            if (gateway is null || !preparation.UsesExternalTransport
                || preparation.TrackingRunId is not { } runId) return;

            var completion = await gateway.CompleteAsync(new WorkspaceTestRunMeterCompletion(
                runId, cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                0, 0, 0, 0, 0), CancellationToken.None).ConfigureAwait(false);
            if (!completion.IsSuccess) reportWarning(completion.Message);
        }
        catch (OperationCanceledException) when (preparationCancellation.IsCancellationRequested)
        {
            // The gateway cleans up runs interrupted during preparation.
        }
        catch (Exception exception)
        {
            // Preserve the original launch/setup failure or cancellation.
            reportWarning(exception.Message);
        }
        finally
        {
            preparationCancellation.Dispose();
        }
    }

    private async Task<WorkspaceTestRunPreparation> PrepareCoreAsync(
        Func<CancellationToken, Task<WorkspaceTestRunPreparation>> prepare)
        => await prepare(preparationCancellation.Token).ConfigureAwait(false);
}
