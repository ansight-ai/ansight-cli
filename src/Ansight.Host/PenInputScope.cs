namespace Ansight.Host;

internal static class PenInputScope
{
    private static readonly AsyncLocal<string?> repositoryRoot = new();
    public static string? CurrentRepositoryRoot => repositoryRoot.Value;

    public static IDisposable Push(string repositoryRootPath)
    {
        var previous = repositoryRoot.Value;
        repositoryRoot.Value = Path.GetFullPath(repositoryRootPath);

        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? previous;
        private bool disposed;

        public Scope(string? previous)
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
            repositoryRoot.Value = previous;
        }
    }
}
