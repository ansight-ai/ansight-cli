namespace Ansight.Host;

internal static class WorkspaceFixturePath
{
    public static string Resolve(string file, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains('\0')) throw new ArgumentException("Provide a fixture file path.");
        if (Uri.TryCreate(file, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new ArgumentException("Fixture must be a local file, not a URL.");
        if (repositoryRoot is null) return Path.GetFullPath(file);
        if (Path.IsPathRooted(file)) throw new ArgumentException("Task fixture paths must be relative to the task repository root.");
        var root = Path.TrimEndingDirectorySeparator(ResolveLinks(Path.GetFullPath(repositoryRoot)));
        var candidate = ResolveLinks(Path.GetFullPath(file, repositoryRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Task fixture files must remain inside the task repository, including symbolic link targets.");
        return candidate;
    }

    private static string ResolveLinks(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                          ?? throw new ArgumentException("Could not resolve the fixture file's symbolic link.");
        }
        return Path.GetFullPath(current);
    }

}
