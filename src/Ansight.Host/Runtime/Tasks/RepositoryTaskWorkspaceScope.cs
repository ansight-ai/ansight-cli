namespace Ansight.Host.Runtime.Tasks;

/// <summary>Overrides task discovery only within one async workspace test run.</summary>
internal static class RepositoryTaskWorkspaceScope
{
    private static readonly AsyncLocal<RepositoryTaskWorkspaceBinding?> current = new();

    public static string Resolve(string appId, string defaultRootPath)
        => current.Value is { } active && string.Equals(active.AppId, appId, StringComparison.Ordinal)
            ? active.RootPath
            : defaultRootPath;

    public static IDisposable Push(string appId, string rootPath)
    {
        var previous = current.Value;
        current.Value = new RepositoryTaskWorkspaceBinding(appId, rootPath);

        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly RepositoryTaskWorkspaceBinding? previous;

        public Scope(RepositoryTaskWorkspaceBinding? previous)
        {
            this.previous = previous;
        }

        public void Dispose() => current.Value = previous;
    }
}
