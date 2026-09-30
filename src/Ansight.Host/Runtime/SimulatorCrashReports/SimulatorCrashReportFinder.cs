namespace Ansight.Host.Runtime.SimulatorCrashReports;

internal static class SimulatorCrashReportFinder
{
    internal static readonly TimeSpan SessionStartTolerance = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumCaptureLag = TimeSpan.FromSeconds(45);

    public static IReadOnlyList<SimulatorCrashReport> FindMatches(
        string diagnosticReportsDirectoryPath,
        SimulatorCrashReportMatchContext context)
    {
        if (string.IsNullOrWhiteSpace(diagnosticReportsDirectoryPath)
            || !Directory.Exists(diagnosticReportsDirectoryPath))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(diagnosticReportsDirectoryPath, "*.ips", SearchOption.TopDirectoryOnly)
                .Where(filePath => IsRecentCandidate(filePath, context))
                .Select(TryParse)
                .Where(report => IsMatch(report, context))
                .Select(report => report!)
                .OrderBy(report => report.CapturedAtUtc)
                .ThenByDescending(report => VersionsMatch(report, context))
                .ThenBy(report => report.IncidentId, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsRecentCandidate(string filePath, SimulatorCrashReportMatchContext context)
    {
        var earliestRelevantWrite = context.SessionStartedAtUtc.ToUniversalTime() - SessionStartTolerance;
        return File.GetLastWriteTimeUtc(filePath) >= earliestRelevantWrite.UtcDateTime;
    }

    private static SimulatorCrashReport? TryParse(string filePath)
        => SimulatorCrashReportParser.TryParse(filePath, out var report) ? report : null;

    private static bool IsMatch(SimulatorCrashReport? report, SimulatorCrashReportMatchContext context)
    {
        if (report is null
            || report.ProcessId != context.ProcessId
            || !string.Equals(report.BundleId, context.BundleId, StringComparison.Ordinal))
        {
            return false;
        }

        var earliestCapture = context.SessionStartedAtUtc.ToUniversalTime() - SessionStartTolerance;
        var latestCapture = context.DisconnectedAtUtc.ToUniversalTime() + MaximumCaptureLag;
        return report.CapturedAtUtc >= earliestCapture && report.CapturedAtUtc <= latestCapture;
    }

    private static bool VersionsMatch(SimulatorCrashReport report, SimulatorCrashReportMatchContext context)
    {
        var appVersionMatches = string.IsNullOrWhiteSpace(context.AppVersion)
                                || string.Equals(report.AppVersion, context.AppVersion, StringComparison.Ordinal);
        var buildVersionMatches = string.IsNullOrWhiteSpace(context.BuildVersion)
                                  || string.Equals(report.BuildVersion, context.BuildVersion, StringComparison.Ordinal);
        return appVersionMatches && buildVersionMatches;
    }
}
