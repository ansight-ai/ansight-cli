namespace Ansight.Infrastructure.Logging;

internal static class LogRetentionPolicy
{
    private static readonly string[] DefaultSearchPatterns =
    [
        "*.log",
        "*.jsonl"
    ];

    public static int DeleteExpiredLogs(string logsFolderPath)
    {
        return DeleteExpiredFiles(
            logsFolderPath,
            LoggingConstants.RetainedLogsMaximumTime,
            DateTimeOffset.UtcNow,
            DefaultSearchPatterns);
    }

    internal static int DeleteExpiredFiles(
        string logsFolderPath,
        TimeSpan retention,
        DateTimeOffset nowUtc,
        IReadOnlyList<string>? searchPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(logsFolderPath)
            || retention <= TimeSpan.Zero
            || !Directory.Exists(logsFolderPath))
        {
            return 0;
        }

        var cutoffUtc = nowUtc.ToUniversalTime().Subtract(retention).UtcDateTime;
        var deletedCount = 0;
        foreach (var filePath in EnumerateCandidateFiles(logsFolderPath, searchPatterns))
        {
            if (!TryGetLastWriteTimeUtc(filePath, out var lastWriteTimeUtc)
                || lastWriteTimeUtc >= cutoffUtc
                || !TryDeleteFile(filePath))
            {
                continue;
            }

            deletedCount++;
        }

        return deletedCount;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(
        string logsFolderPath,
        IReadOnlyList<string>? searchPatterns)
    {
        var resolvedSearchPatterns = searchPatterns is { Count: > 0 }
            ? searchPatterns
            : DefaultSearchPatterns;
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var searchPattern in resolvedSearchPatterns)
        {
            if (string.IsNullOrWhiteSpace(searchPattern))
            {
                continue;
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(
                    logsFolderPath,
                    searchPattern,
                    SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var filePath in matches)
            {
                if (filePaths.Add(filePath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static bool TryGetLastWriteTimeUtc(string filePath, out DateTime lastWriteTimeUtc)
    {
        try
        {
            lastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lastWriteTimeUtc = default;
            return false;
        }
    }

    private static bool TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
