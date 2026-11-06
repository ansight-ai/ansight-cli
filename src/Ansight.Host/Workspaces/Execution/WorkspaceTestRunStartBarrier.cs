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
