namespace Ansight.Host.Runtime.WebSocketSessions;

internal sealed class SessionConnectionRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, LiveSessionConnection> connectionsBySessionId = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> GetSessionIds()
    {
        lock (gate)
        {
            return connectionsBySessionId.Keys
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal bool Contains(string sessionId)
    {
        lock (gate)
        {
            return connectionsBySessionId.ContainsKey(sessionId);
        }
    }

    internal LiveSessionConnection? Get(string sessionId)
    {
        lock (gate)
        {
            connectionsBySessionId.TryGetValue(sessionId, out var connection);
            return connection;
        }
    }

    internal LiveSessionConnection? Register(string sessionId, LiveSessionConnection connection)
    {
        lock (gate)
        {
            connectionsBySessionId.TryGetValue(sessionId, out var existing);
            connectionsBySessionId[sessionId] = connection;
            return ReferenceEquals(existing, connection) ? null : existing;
        }
    }

    internal bool Unregister(string sessionId, LiveSessionConnection connection)
    {
        lock (gate)
        {
            if (!connectionsBySessionId.TryGetValue(sessionId, out var existing)
                || !ReferenceEquals(existing, connection))
            {
                return false;
            }

            connectionsBySessionId.Remove(sessionId);
            return true;
        }
    }

    internal IReadOnlyList<LiveSessionConnection> GetConnections()
    {
        lock (gate)
        {
            return connectionsBySessionId.Values.ToArray();
        }
    }
}
