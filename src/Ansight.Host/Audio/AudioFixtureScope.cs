namespace Ansight.Host.Audio;

internal static class AudioFixtureScope
{
    private static readonly AsyncLocal<string?> repositoryRoot = new();
    public static string? CurrentRepositoryRoot => repositoryRoot.Value;

    public static IDisposable Push(string repositoryRootPath)
    {
        var previous = repositoryRoot.Value;
        repositoryRoot.Value = Path.GetFullPath(repositoryRootPath);
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            repositoryRoot.Value = previous;
        }
    }
}
