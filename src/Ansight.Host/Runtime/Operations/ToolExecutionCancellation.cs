namespace Ansight.Host.Runtime.Operations;

internal static class ToolExecutionCancellation
{
    private static readonly AsyncLocal<CancellationToken?> current = new();

    public static CancellationToken Current => current.Value ?? CancellationToken.None;

    public static IDisposable Push(CancellationToken cancellationToken)
    {
        var previous = current.Value;
        current.Value = cancellationToken;

        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private CancellationToken? previous;
        private bool disposed;

        public Scope(CancellationToken? previous)
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
            current.Value = previous;
            previous = null;
        }
    }
}
