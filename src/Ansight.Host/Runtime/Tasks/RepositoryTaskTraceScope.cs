namespace Ansight.Host.Runtime.Tasks;

/// <summary>Flows the owning run's trace choice through host operations and composed tasks.</summary>
internal static class RepositoryTaskTraceScope
{
    private static readonly AsyncLocal<bool> enabled = new();

    public static bool IsEnabled => enabled.Value;

    public static IDisposable Begin(bool captureTrace)
    {
        var scope = new Scope(enabled.Value);
        enabled.Value = captureTrace;
        return scope;
    }

    private sealed class Scope(bool previousValue) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            enabled.Value = previousValue;
            disposed = true;
        }
    }
}
