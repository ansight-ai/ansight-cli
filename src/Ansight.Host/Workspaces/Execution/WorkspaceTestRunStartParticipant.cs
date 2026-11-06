namespace Ansight.Host.Workspaces.Execution;

internal sealed class WorkspaceTestRunStartParticipant : IDisposable
{
    private readonly WorkspaceTestRunStartBarrier barrier;
    private int arrived;

    public WorkspaceTestRunStartParticipant(
        WorkspaceTestRunStartBarrier barrier)
    {
        this.barrier = barrier;
    }

    public async Task ArriveAndWaitAsync(CancellationToken cancellationToken)
    {
        Arrive();
        await barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
        => Arrive();

    private void Arrive()
    {
        if (Interlocked.Exchange(ref arrived, 1) == 0)
        {
            barrier.Arrive();
        }
    }
}
