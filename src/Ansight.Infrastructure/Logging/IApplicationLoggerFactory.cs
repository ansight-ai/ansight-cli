using System;
using Ansight.Infrastructure.Logging;

namespace Ansight.Infrastructure.Logging;

public interface IApplicationLoggerFactory : ILoggerFactory
{
    string LogFilePath { get; }

    ILogFilter Filter { get; }

    LogLevel MinimumLogLevel { get; }

    void SetMinimumLogLevel(LogLevel logLevel);
}
