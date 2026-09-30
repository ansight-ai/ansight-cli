namespace Ansight.Host.Workspaces.Execution;

internal sealed class WorkspaceTestRunStartBarrier
{
    private readonly TaskCompletionSource release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int remainingParticipants;

    public WorkspaceTestRunStartBarrier(int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);
        remainingParticipants = participantCount;
    }

    public WorkspaceTestRunStartParticipant CreateParticipant()
        => new(this);

    internal void Arrive()
    {
        if (Interlocked.Decrement(ref remainingParticipants) == 0)
        {
            release.TrySetResult();
        }
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
        => release.Task.WaitAsync(cancellationToken);
}

internal sealed class WorkspaceTestRunStartParticipant(
    WorkspaceTestRunStartBarrier barrier) : IDisposable
{
    private int arrived;

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
