using System.Diagnostics;
using System.Text.Json;
using Ansight.Host;
using Ansight.Host.Workspaces;

namespace Ansight.Cli.Commands.App;

internal sealed class AppExecutionRunStartParticipant : IDisposable
{
    private readonly AppExecutionRunStartBarrier barrier;
    private int arrived;

    public AppExecutionRunStartParticipant(
        AppExecutionRunStartBarrier barrier)
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
