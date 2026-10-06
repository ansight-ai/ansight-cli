namespace Ansight.Host.Workspaces;

internal static class BoundedAuditHistory
{
    internal const int MaximumRuns = 500;
    internal const long MaximumBytes = 256L * 1_024 * 1_024;

    internal static void Prune(string directoryPath, string currentFilePath, bool includeTraceDirectories)
    {
        var runs = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => new RunFiles(path, includeTraceDirectories))
            .OrderByDescending(static run => run.TimestampUtc)
            .ToArray();
        var totalBytes = runs.Sum(static run => run.SizeBytes);
        var remainingCount = runs.Length;
        for (var index = runs.Length - 1; index >= 0; index--)
        {
            if (remainingCount <= MaximumRuns && totalBytes <= MaximumBytes)
            {
                break;
            }

            var run = runs[index];
            if (string.Equals(run.AuditPath, currentFilePath, StringComparison.Ordinal))
            {
                continue;
            }

            run.Delete();
            remainingCount--;
            totalBytes -= run.SizeBytes;
        }

        if (totalBytes > MaximumBytes)
        {
            var currentRun = runs.First(run => string.Equals(run.AuditPath, currentFilePath, StringComparison.Ordinal));
            currentRun.Delete();
            throw new IOException("The audit run exceeds the local history size limit.");
        }
    }

    private sealed class RunFiles
    {
        private readonly string meteringPath;
        private readonly string traceDirectoryPath;

        internal RunFiles(string auditPath, bool includeTraceDirectory)
        {
            AuditPath = auditPath;
            meteringPath = auditPath + ".metering";
            traceDirectoryPath = includeTraceDirectory
                ? Path.Combine(Path.GetDirectoryName(auditPath)!, Path.GetFileNameWithoutExtension(auditPath) + ".trace")
                : string.Empty;
            TimestampUtc = File.GetLastWriteTimeUtc(auditPath);
            SizeBytes = new FileInfo(auditPath).Length;
            if (File.Exists(meteringPath))
            {
                SizeBytes += new FileInfo(meteringPath).Length;
            }

            if (Directory.Exists(traceDirectoryPath))
            {
                SizeBytes += Directory.EnumerateFiles(traceDirectoryPath, "*", SearchOption.AllDirectories)
                    .Sum(static path => new FileInfo(path).Length);
            }
        }

        internal string AuditPath { get; }
        internal DateTime TimestampUtc { get; }
        internal long SizeBytes { get; }

        internal void Delete()
        {
            File.Delete(AuditPath);
            if (File.Exists(meteringPath))
            {
                File.Delete(meteringPath);
            }

            if (Directory.Exists(traceDirectoryPath))
            {
                Directory.Delete(traceDirectoryPath, recursive: true);
            }
        }
    }
}
