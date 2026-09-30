using Ansight.Infrastructure.Logging;
using Ansight.Host.Tests.Unit.Runtime;

namespace Ansight.Host.Tests.Unit.Logging;

public sealed class ApplicationLoggerTests
{
    [Fact]
    public void ApplicationLoggerFilter_DefaultsToInformation()
    {
        var filter = new ApplicationLoggerFilter();

        Assert.Equal(LogLevel.Information, filter.MinimumLogLevel);
        Assert.False(filter.CanLog("test", "debug", LogLevel.Debug));
        Assert.True(filter.CanLog("test", "info", LogLevel.Information));
    }

    [Fact]
    public void ApplicationLoggerFactory_SetMinimumLogLevel_FiltersCreatedLoggers()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var factory = new ApplicationLoggerFactory(temporaryDirectory.RootPath);
        var tag = $"LogLevelTest{Guid.NewGuid():N}";
        var logger = factory.Create(tag);

        factory.SetMinimumLogLevel(LogLevel.Warning);

        logger.Info("not captured");
        logger.Warning("captured");

        Assert.Equal(LogLevel.Warning, factory.MinimumLogLevel);
        var line = Assert.Single(
            MutableApplicationLogBuffer.Instance.Logs,
            log => log.Contains(tag, StringComparison.Ordinal));
        Assert.Contains("captured", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_WithParameters_UsesLoggerTagInFormattedOutput()
    {
        var buffer = new MutableApplicationLogBuffer();
        var logger = new ApplicationLogger("SessionAnalysisService", new ApplicationLoggerFilter(), 1234, buffer);

        logger.Event("analysis_started", ("sessionId", "session-001"));

        var line = Assert.Single(buffer.Logs);
        Assert.Contains("SessionAnalysisService: analysis_started", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_WithProperties_UsesLoggerTagInFormattedOutput()
    {
        var buffer = new MutableApplicationLogBuffer();
        var logger = new ApplicationLogger("WorkspaceViewModel", new ApplicationLoggerFilter(), 1234, buffer);

        logger.Event(
            "workspace_initialized",
            new Dictionary<string, object?>
            {
                ["workspaceId"] = "workspace-001"
            });

        var line = Assert.Single(buffer.Logs);
        Assert.Contains("WorkspaceViewModel: workspace_initialized", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_NotifiesAnalyticsSink()
    {
        var buffer = new MutableApplicationLogBuffer();
        var logger = new CapturingApplicationLogger("WorkspaceViewModel", new ApplicationLoggerFilter(), 1234, buffer);

        logger.Event("workspace_initialized");

        var line = Assert.Single(buffer.Logs);
        Assert.Contains("WorkspaceViewModel: workspace_initialized", line, StringComparison.Ordinal);
        Assert.Equal(1, logger.EventCount);
        Assert.Equal("workspace_initialized", logger.LastEventName);
    }

    [Fact]
    public void LogRetentionPolicy_DeleteExpiredFiles_RemovesOnlyStaleLogFiles()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var nestedLogsPath = Path.Combine(temporaryDirectory.RootPath, "host-operation-traffic");
        Directory.CreateDirectory(nestedLogsPath);
        var nowUtc = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
        var staleLegacyDesktopLogPath = CreateFile(
            Path.Combine(temporaryDirectory.RootPath, "legacy-desktop-old.log"),
            nowUtc.AddDays(-29));
        var recentLegacyDesktopLogPath = CreateFile(
            Path.Combine(temporaryDirectory.RootPath, "legacy-desktop-recent.log"),
            nowUtc.AddDays(-27));
        var staleTrafficLogPath = CreateFile(
            Path.Combine(nestedLogsPath, "host-operation-traffic-old.jsonl"),
            nowUtc.AddDays(-29));
        var staleNonLogPath = CreateFile(
            Path.Combine(temporaryDirectory.RootPath, "notes.txt"),
            nowUtc.AddDays(-90));

        var deletedCount = LogRetentionPolicy.DeleteExpiredFiles(
            temporaryDirectory.RootPath,
            TimeSpan.FromDays(28),
            nowUtc);

        Assert.Equal(2, deletedCount);
        Assert.False(File.Exists(staleLegacyDesktopLogPath));
        Assert.False(File.Exists(staleTrafficLogPath));
        Assert.True(File.Exists(recentLegacyDesktopLogPath));
        Assert.True(File.Exists(staleNonLogPath));
    }

    private sealed class CapturingApplicationLogger(
        string tag,
        ILogFilter filter,
        int processId,
        IMutableApplicationLogBuffer applicationLogBuffer)
        : ApplicationLogger(tag, filter, processId, applicationLogBuffer)
    {
        public int EventCount { get; private set; }

        public string LastEventName { get; private set; } = string.Empty;

        protected override void OnEvent(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
        {
            EventCount++;
            LastEventName = eventName;
        }

        protected override void OnEvent(string eventName, params LogEventParameter[] parameters)
        {
            EventCount++;
            LastEventName = eventName;
        }
    }

    private static string CreateFile(string filePath, DateTimeOffset lastWriteTimeUtc)
    {
        File.WriteAllText(filePath, filePath);
        File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc.UtcDateTime);
        return filePath;
    }
}
