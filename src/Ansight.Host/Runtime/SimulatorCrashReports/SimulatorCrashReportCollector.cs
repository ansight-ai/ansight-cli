namespace Ansight.Host.Runtime.SimulatorCrashReports;

using System.Globalization;
using Ansight.Pairing.Models;
using Ansight.Infrastructure.Logging;

[Export(typeof(ISimulatorCrashReportCollector))]
internal sealed class SimulatorCrashReportCollector : ISimulatorCrashReportCollector
{
    private const string ArtifactSource = "ansight.simulator.crash-report";
    private const string ArtifactRootAlias = "simulator-crash-reports";
    private static readonly ILogger log = Logger.Create();
    private static readonly TimeSpan[] PollDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15)
    ];

    private readonly IRuntimeState runtimeState;
    private readonly string applicationTempPath;
    private readonly string diagnosticReportsDirectoryPath;

    [ImportingConstructor]
    public SimulatorCrashReportCollector(IRuntimeState runtimeState, IApplicationPaths applicationPaths)
        : this(
            runtimeState,
            applicationPaths.ApplicationTempPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Logs",
                "DiagnosticReports"))
    {
    }

    internal SimulatorCrashReportCollector(
        IRuntimeState runtimeState,
        string applicationTempPath,
        string diagnosticReportsDirectoryPath)
    {
        this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        this.applicationTempPath = string.IsNullOrWhiteSpace(applicationTempPath)
            ? throw new ArgumentException("Application temp path is required.", nameof(applicationTempPath))
            : applicationTempPath.Trim();
        this.diagnosticReportsDirectoryPath = string.IsNullOrWhiteSpace(diagnosticReportsDirectoryPath)
            ? throw new ArgumentException("Diagnostic reports directory path is required.", nameof(diagnosticReportsDirectoryPath))
            : diagnosticReportsDirectoryPath.Trim();
    }

    public void SearchAndAttach(string sessionId, DateTimeOffset disconnectedAtUtc)
    {
        if (!IsAppleDesktopHost()
            || !TryCreateMatchContext(sessionId, disconnectedAtUtc, out var context)
            || context is null)
        {
            return;
        }

        Task.Run(() => SearchAndAttachAsync(sessionId.Trim(), context)).SafeFireAndForget();
    }

    internal bool TryCreateMatchContext(
        string sessionId,
        DateTimeOffset disconnectedAtUtc,
        out SimulatorCrashReportMatchContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(sessionId)
            || !runtimeState.TryGetSessionSnapshot(sessionId.Trim(), out var snapshot)
            || snapshot?.DeviceProfile is not { } profile
            || !IsIosSimulator(profile)
            || string.IsNullOrWhiteSpace(profile.App?.AppId)
            || profile.App.ProcessId is not > 0)
        {
            return false;
        }

        context = new SimulatorCrashReportMatchContext(
            profile.App.AppId.Trim(),
            profile.App.ProcessId.Value,
            snapshot.CreatedUtc.ToUniversalTime(),
            disconnectedAtUtc.ToUniversalTime(),
            NullIfWhiteSpace(profile.App.VersionName),
            NullIfWhiteSpace(profile.App.BuildNumber ?? profile.App.VersionCode));
        return true;
    }

    private async Task SearchAndAttachAsync(string sessionId, SimulatorCrashReportMatchContext context)
    {
        var processedIncidentIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var delay in PollDelays)
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                var reports = SimulatorCrashReportFinder.FindMatches(diagnosticReportsDirectoryPath, context);
                foreach (var report in reports)
                {
                    if (!processedIncidentIds.Add(report.IncidentId))
                    {
                        continue;
                    }

                    if (!AttachReport(sessionId, report))
                    {
                        processedIncidentIds.Remove(report.IncidentId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Info($"simulator_crash_report_search_failed sessionId={sessionId} message={FormatLogValue(ex.Message)}");
        }
    }

    internal bool AttachReport(string sessionId, SimulatorCrashReport report)
    {
        var snapshotId = $"simulator-crash-{FileNameUtil.Sanitize(report.IncidentId)}";
        if (runtimeState.TryGetSessionSnapshot(sessionId, out var currentSnapshot)
            && currentSnapshot?.ArtifactSnapshots.Any(snapshot =>
                string.Equals(snapshot.SnapshotId, snapshotId, StringComparison.Ordinal)) == true)
        {
            return true;
        }

        var artifactDirectoryName = snapshotId;
        var sourceDirectoryPath = Path.Combine(
            applicationTempPath,
            "simulator-crash-reports",
            FileNameUtil.Sanitize(sessionId),
            artifactDirectoryName);
        var reportFileName = Path.GetFileName(report.FilePath);
        if (string.IsNullOrWhiteSpace(reportFileName))
        {
            reportFileName = $"{artifactDirectoryName}.ips";
        }
        var sourceFilePath = Path.Combine(sourceDirectoryPath, reportFileName);

        try
        {
            Directory.CreateDirectory(sourceDirectoryPath);
            File.Copy(report.FilePath, sourceFilePath, overwrite: true);
            var sourceFileInfo = new FileInfo(sourceFilePath);
            var entry = new SessionArtifactEntry
            {
                Name = reportFileName,
                RootAlias = ArtifactRootAlias,
                RelativePath = reportFileName,
                SnapshotRelativePath = reportFileName,
                Kind = "file",
                SizeBytes = sourceFileInfo.Length,
                FileExtension = ".ips",
                MimeType = "application/json",
                LastModifiedUtc = sourceFileInfo.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                ArchiveRelativePath = reportFileName
            };
            var snapshot = new SessionArtifactSnapshot
            {
                SnapshotId = snapshotId,
                CapturedAtUtc = report.CapturedAtUtc,
                Source = ArtifactSource,
                RootAlias = ArtifactRootAlias,
                RootPath = ArtifactRootAlias,
                RelativePath = reportFileName,
                Name = $"{report.ProcessName} crash report",
                Kind = "crash-report",
                ArtifactDirectoryName = artifactDirectoryName,
                DirectoryCount = 0,
                FileCount = 1,
                ByteCount = sourceFileInfo.Length,
                Truncated = false,
                Entries = [entry]
            };

            var result = runtimeState.AddSessionArtifactSnapshot(sessionId, snapshot, sourceDirectoryPath);
            if (!result.IsSuccess)
            {
                log.Info($"simulator_crash_report_attach_failed sessionId={sessionId} incidentId={report.IncidentId} message={FormatLogValue(result.Message)}");
                return false;
            }

            runtimeState.AddSessionLog(
                sessionId,
                $"Attached Simulator crash report '{reportFileName}' for process {report.ProcessId}.");
            log.Info($"simulator_crash_report_attached sessionId={sessionId} incidentId={report.IncidentId} processId={report.ProcessId} simulatorUdid={FormatLogValue(report.SimulatorUdid)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Info($"simulator_crash_report_attach_failed sessionId={sessionId} incidentId={report.IncidentId} message={FormatLogValue(ex.Message)}");
            return false;
        }
        finally
        {
            TryDeleteDirectory(sourceDirectoryPath);
        }
    }

    private static bool IsIosSimulator(DeviceAppProfile profile)
    {
        var device = profile.Device;
        if (device?.IsEmulator != true && device?.IsVirtual != true)
        {
            return false;
        }

        return string.Equals(device.OsName, "ios", StringComparison.OrdinalIgnoreCase)
               || profile.Runtime?.Stack?.Any(entry =>
                   string.Equals(entry.Name, "ios", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static bool IsAppleDesktopHost()
        => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst();

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // The persisted session already has its own copy. Temporary cleanup is best effort.
        }
    }

    private static string FormatLogValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
