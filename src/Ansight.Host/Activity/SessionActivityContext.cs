namespace Ansight.Host.Activity;

/// <summary>Immutable request context; scopes flow through awaits and remain isolated between requests.</summary>
public sealed record SessionActivityContext(ActivityCaller Caller, string? RunId = null, string? BatchRunId = null)
{
    private static readonly AsyncLocal<SessionActivityContext?> current = new();
    public static SessionActivityContext? Current => current.Value;

    public static IDisposable Push(SessionActivityContext context)
    {
        var previous = current.Value;
        current.Value = context;
        return new Scope(() => current.Value = previous);
    }

    public static IDisposable ExecuteAs(string kind, string name, string runId, string? batchRunId = null)
    {
        var parent = Current ?? new SessionActivityContext(new ActivityCaller());
        return Push(parent with
        {
            Caller = parent.Caller with
            {
                Kind = kind, Name = name, ExecutorAttribution = "host-generated",
                InitiatorKind = parent.Caller.InitiatorKind ?? parent.Caller.Kind,
                InitiatorName = parent.Caller.InitiatorName ?? parent.Caller.Name,
                InitiatorAttribution = parent.Caller.InitiatorAttribution ?? parent.Caller.ExecutorAttribution
            },
            RunId = ActivityCaller.Limit(runId),
            BatchRunId = ActivityCaller.Limit(batchRunId) ?? parent.BatchRunId
        });
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? restore = restore;
        public void Dispose() => Interlocked.Exchange(ref restore, null)?.Invoke();
    }
}
