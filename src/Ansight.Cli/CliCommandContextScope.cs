namespace Ansight.Cli;

internal sealed class CliCommandContextScope : IDisposable
{
    private readonly Action restore;
    private bool disposed;

    public CliCommandContextScope(Action restore)
    {
        this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        restore();
    }
}
