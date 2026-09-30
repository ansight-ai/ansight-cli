namespace Ansight.Infrastructure.Logging;

public sealed class ConsoleLoggerFactory : ILoggerFactory
{
    private readonly PlatformConsoleLoggerDelegate? platformConsoleLogger;

    public ConsoleLoggerFactory(PlatformConsoleLoggerDelegate? platformConsoleLogger = null)
    {
        this.platformConsoleLogger = platformConsoleLogger;
    }

    public ILogger Create(string tag)
    {
        return new ConsoleLogger(tag, platformConsoleLogger);
    }

    public void Dispose()
    {
    }
}
