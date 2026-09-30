using System;
using System.Diagnostics;

namespace Ansight.Infrastructure.Logging;

public class ConsoleLogger : BaseLogger
{
    private readonly PlatformConsoleLoggerDelegate? platformConsoleLogger;
    public int ProcessId { get; }

    public ConsoleLogger(string context, PlatformConsoleLoggerDelegate? platformConsoleLogger)
        : base(context)
    {
        ProcessId = Process.GetCurrentProcess().Id;
        this.platformConsoleLogger = platformConsoleLogger;
    }

    public override void Log(string tag, string message, LogLevel logLevel = LogLevel.Information)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (platformConsoleLogger != null)
        {
            platformConsoleLogger(tag, message, logLevel);
            return;
        }

        var now = DateTime.Now;
        var lines = message.Split('\n', '\r');

        bool shouldFormat = IsFormattingSuspended == false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var output =  shouldFormat ? LogFormatter.Render(now, tag, line, ProcessId, Thread.CurrentThread.ManagedThreadId, logLevel) : line;
            Console.Out.WriteLine(output);
        }
    }


    public override void Exception(Exception ex)
    {
        Log(Tag, ex.ToString(), LogLevel.Error);
    }

    public override void Event(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
    }

    public override void Event(string eventName, params LogEventParameter[] parameters)
    {
    }
}
