using System;
using System.Diagnostics;
using System.IO;

namespace Ansight.Infrastructure.Logging;

public class ApplicationLoggerFactory : IApplicationLoggerFactory
{
    private readonly string applicationLogsPath;
    private string logFilePath = string.Empty;
    protected LogFileWriter? LogFileWriter { get; private set; }

    protected int ProcessId { get; }

    protected ApplicationLoggerFilter LogFilter { get; }

    public string LogFilePath => logFilePath;


    public ILogFilter Filter => LogFilter;

    public LogLevel MinimumLogLevel => LogFilter.MinimumLogLevel;

    public ApplicationLoggerFactory(string applicationLogsPath, bool writeToFile = false)
    {
        if (string.IsNullOrWhiteSpace(applicationLogsPath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(applicationLogsPath));

        this.applicationLogsPath = applicationLogsPath;

        ProcessId = Process.GetCurrentProcess().Id;

        // ApplicationPaths = applicationPaths;
        LogFilter = new ApplicationLoggerFilter();

        var logsFolderPath = applicationLogsPath;
        if (!Directory.Exists(logsFolderPath))
        {
            Directory.CreateDirectory(logsFolderPath);
        }

        LogRetentionPolicy.DeleteExpiredLogs(logsFolderPath);

        if (writeToFile)
        {
            var resolvedLogFilePath = CreateLogFilePath(logsFolderPath);
            if (string.IsNullOrWhiteSpace(resolvedLogFilePath))
            {
                resolvedLogFilePath = Path.Combine(
                    logsFolderPath,
                    DateTime.UtcNow.ToString(LoggingConstants.FileNameDateFormat) + LoggingConstants.LogFileExtension);
            }

            logFilePath = resolvedLogFilePath;
            LogFileWriter = new LogFileWriter(logFilePath);
        }
    }

    public virtual ILogger Create(string tag)
    {
        return new ApplicationLogger(tag, LogFilter, ProcessId, MutableApplicationLogBuffer.Instance, LogFileWriter);
    }

    public void SetMinimumLogLevel(LogLevel logLevel)
    {
        LogFilter.SetMinimumLogLevel(logLevel);
    }

    public void Dispose()
    {
        if (LogFileWriter != null)
        {
            LogFileWriter.Dispose();
            LogFileWriter = null;
        }
    }

    protected virtual string CreateLogFilePath(string logsFolderPath)
    {
        var logFileId = DateTime.UtcNow.ToString(LoggingConstants.FileNameDateFormat);
        var logFileName = logFileId + LoggingConstants.LogFileExtension;
        return Path.Combine(logsFolderPath, logFileName);
    }
}
