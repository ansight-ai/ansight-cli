namespace Ansight.Host.Runtime.State;

internal sealed class FocusedControlStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, RuntimeFocusedControlSnapshot> snapshotsBySessionId = new(StringComparer.Ordinal);

    internal IReadOnlyList<RuntimeFocusedControlSnapshot> GetSnapshots()
    {
        lock (gate)
        {
            return snapshotsBySessionId.Values
                .OrderByDescending(snapshot => snapshot.FocusedAtUtc)
                .Select(snapshot => snapshot.Clone())
                .ToArray();
        }
    }

    internal void Set(RuntimeFocusedControlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SessionId))
        {
            return;
        }

        lock (gate)
        {
            snapshotsBySessionId[snapshot.SessionId] = snapshot.Clone();
        }
    }

    internal void Clear(string? sessionId)
    {
        lock (gate)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                snapshotsBySessionId.Clear();
                return;
            }

            snapshotsBySessionId.Remove(sessionId);
        }
    }
}
