namespace Ansight.Host.Models.Session;

public static class SessionArtifactPathNormalizer
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim()
            .Replace('\\', '/')
            .Trim('/');
    }

    public static string GetParentPath(string? path)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex <= 0 ? string.Empty : normalized[..separatorIndex];
    }
}
