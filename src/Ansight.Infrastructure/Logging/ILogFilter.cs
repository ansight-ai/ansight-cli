

namespace Ansight.Infrastructure.Logging;

public interface ILogFilter
{
    bool CanLog(string tag, string message, LogLevel logLevel);
}
