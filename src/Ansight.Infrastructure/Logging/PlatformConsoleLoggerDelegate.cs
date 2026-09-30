namespace Ansight.Infrastructure.Logging;

public delegate bool PlatformConsoleLoggerDelegate(
    string tag,
    string message,
    LogLevel logLevel);
