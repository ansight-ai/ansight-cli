namespace Ansight.Host.Runtime.State;

internal sealed partial class RuntimeState
{
    public bool TryGetSessionLiveUpdate(
        string sessionId,
        SessionLiveUpdateCursor cursor,
        out SessionLiveUpdate? update)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            update = null;
            return false;
        }

        lock (gate)
        {
            if (!sessionsById.TryGetValue(sessionId.Trim(), out var session))
            {
                update = null;
                return false;
            }

            update = SessionLiveUpdateBuilder.Create(session, cursor);
            return true;
        }
    }
}
