namespace Ansight.Host.Runtime.Tasks;

/// <summary>Overrides task discovery only within one async workspace test run.</summary>
internal static class RepositoryTaskWorkspaceScope
{
    private static readonly AsyncLocal<(string AppId, string RootPath)?> current = new();

    public static string Resolve(string appId, string defaultRootPath)
        => current.Value is { } active && string.Equals(active.AppId, appId, StringComparison.Ordinal)
            ? active.RootPath
            : defaultRootPath;

    public static IDisposable Push(string appId, string rootPath)
    {
        var previous = current.Value;
        current.Value = (appId, rootPath);
        return new Scope(previous);
    }

    private sealed class Scope((string AppId, string RootPath)? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
