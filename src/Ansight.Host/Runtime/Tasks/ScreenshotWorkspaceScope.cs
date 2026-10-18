namespace Ansight.Host.Runtime.Tasks;

/// <summary>The calling task owns the workspace used to resolve image fixtures.</summary>
internal static class ScreenshotWorkspaceScope
{
    private static readonly AsyncLocal<string?> repositoryRoot = new();
    public static string? RepositoryRoot => repositoryRoot.Value;

    public static IDisposable Push(string root)
    {
        var previous = repositoryRoot.Value;
        repositoryRoot.Value = Path.GetFullPath(root);
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => repositoryRoot.Value = previous;
    }
}
