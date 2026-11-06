using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed class AppExecutionRunStartBarrier
{
    private readonly TaskCompletionSource release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int remainingParticipants;

    public AppExecutionRunStartBarrier(int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);
        remainingParticipants = participantCount;
    }

    public AppExecutionRunStartParticipant CreateParticipant()
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
