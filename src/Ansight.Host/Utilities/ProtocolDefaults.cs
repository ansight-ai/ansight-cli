namespace Ansight.Host.Utilities;

internal static class ProtocolDefaults
{
    private static readonly Lock gate = new();
    private static ProtocolDefaultValues? overrideValues;

    public static int DiscoveryPort => GetCurrent().DiscoveryPort;

    public static int WebSocketPort => GetCurrent().WebSocketPort;

    public static string WebSocketPath => GetCurrent().WebSocketPath;

    public static int WebSocketSessionPortRangeStart => GetCurrent().WebSocketSessionPortRangeStart;

    public static int WebSocketSessionPortRangeEnd => GetCurrent().WebSocketSessionPortRangeEnd;

    internal static IDisposable PushOverride(
        int discoveryPort,
        int webSocketPort,
        string webSocketPath,
        int webSocketSessionPortRangeStart,
        int webSocketSessionPortRangeEnd)
    {
        lock (gate)
        {
            var previous = overrideValues;
            overrideValues = new ProtocolDefaultValues(
                discoveryPort,
                webSocketPort,
                webSocketPath,
                webSocketSessionPortRangeStart,
                webSocketSessionPortRangeEnd);
            return new OverrideScope(previous);
        }
    }

    private static ProtocolDefaultValues GetCurrent()
    {
        lock (gate)
        {
            return overrideValues ?? ProtocolDefaultValues.Default;
        }
    }

    private sealed class OverrideScope : IDisposable
    {
        private readonly ProtocolDefaultValues? previous;
        private bool disposed;

        public OverrideScope(ProtocolDefaultValues? previous)
        {
            this.previous = previous;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (gate)
            {
                overrideValues = previous;
            }
        }
    }

    private sealed record ProtocolDefaultValues(
        int DiscoveryPort,
        int WebSocketPort,
        string WebSocketPath,
        int WebSocketSessionPortRangeStart,
        int WebSocketSessionPortRangeEnd)
    {
        public static ProtocolDefaultValues Default { get; } = new(
            45123,
            45124,
            "/ws",
            56500,
            56599);
    }
}
