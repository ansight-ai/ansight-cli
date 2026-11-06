using System.Diagnostics;
using Ansight.Host.Runtime.Operations;
using Ansight.Host.Runtime.Tasks;
using Ansight.Host.SimulatorAgent.Observations;

namespace Ansight.Host.Workspaces.Execution;

internal sealed class WorkspaceSessionConnectionTracker
{
    private readonly int requiredSamples;
    private string? sessionId;
    private int successfulSamples;

    public WorkspaceSessionConnectionTracker(int requiredSamples)
    {
        if (requiredSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredSamples));
        }

        this.requiredSamples = requiredSamples;
    }

    public bool Observe(string observedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observedSessionId);

        if (string.Equals(sessionId, observedSessionId, StringComparison.Ordinal))
        {
            successfulSamples++;
        }
        else
        {
            sessionId = observedSessionId;
            successfulSamples = 1;
        }

        return successfulSamples >= requiredSamples;
    }

    public void Reset()
    {
        sessionId = null;
        successfulSamples = 0;
    }
}
