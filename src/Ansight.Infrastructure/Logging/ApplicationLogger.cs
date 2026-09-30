using System.Text;
using System.Text.Json;

namespace Ansight.Infrastructure.Logging;

public class ApplicationLogger : ILogger
{
    private readonly ILogFilter filter;
    private readonly IMutableApplicationLogBuffer applicationLogBuffer;
    private readonly LogFileWriter? logFileWriter;

    private readonly object suspendLogFormattingScopeLock = new object();
    private SuspendLogFormattingScope? suspendLogFormattingScope;

    public string LogFilePath => logFileWriter?.LogFilePath ?? string.Empty;

    public ApplicationLogger(string tag,
        ILogFilter filter,
        int processId,
        IMutableApplicationLogBuffer applicationLogBuffer,
        LogFileWriter? logFileWriter = null,
        bool ignoreEmptyValues = true)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException($"'{nameof(tag)}' cannot be null or whitespace.", nameof(tag));
        }

        Tag = tag;
        this.filter = filter ?? throw new ArgumentNullException(nameof(filter));
        ProcessId = processId;
        IgnoreEmptyValues = ignoreEmptyValues;
        this.applicationLogBuffer = applicationLogBuffer ?? throw new ArgumentNullException(nameof(applicationLogBuffer));
        this.logFileWriter = logFileWriter;
    }

    public void Error(string message)
    {
        Log(Tag, message, LogLevel.Error);
    }

    public void Warning(string message)
    {
        Log(Tag, message, LogLevel.Warning);
    }

    public void Info(string message)
    {
        Log(Tag, message, LogLevel.Information);
    }

    public void Debug(string message)
    {
        Log(Tag, message, LogLevel.Debug);
    }

    public void Verbose(string message)
    {
        Log(Tag, message, LogLevel.Verbose);
    }

    public string Tag { get; }
    public int ProcessId { get; }
    public bool IgnoreEmptyValues { get; }

    public void Log(string tag, string message, LogLevel logLevel)
    {
        if (string.IsNullOrWhiteSpace(message) && IgnoreEmptyValues)
        {
            return;
        }

        if (!filter.CanLog(tag, message, logLevel))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = Tag;
        }

        bool shouldFormat = true;

        lock (suspendLogFormattingScopeLock)
        {
            var isSuspended = suspendLogFormattingScope != null && suspendLogFormattingScope.IsSuspended;
            shouldFormat = !isSuspended;
        }


        var now = DateTime.Now;
        var threadId = Thread.CurrentThread.ManagedThreadId;

        var didPlatformLog = PlatformLog(tag, message, logLevel);
        var shouldWriteToConsole = !didPlatformLog;

        var logLines = new List<string>();
        var messageSpan = message.AsSpan();
        var messageMemory = message.AsMemory();

        var lineStart = 0;

        for (var i = 0; i <= messageSpan.Length; i++)
        {
            var atEnd = i == messageSpan.Length;
            var current = atEnd ? '\n' : messageSpan[i];

            if (!atEnd && current != '\n' && current != '\r')
            {
                continue;
            }

            var lineLength = i - lineStart;
            var shouldIncludeLine = lineLength > 0 || !IgnoreEmptyValues;

            if (shouldIncludeLine)
            {
                var lineMemory = messageMemory.Slice(lineStart, lineLength);
                var lineText = new string(lineMemory.Span);

                var output = shouldFormat
                    ? LogFormatter.Render(now, tag, lineMemory, ProcessId, threadId, logLevel)
                    : lineText;

                logLines.Add(output);

                if (shouldWriteToConsole)
                {
                    Console.Out.WriteLine(output);
                }

                logFileWriter?.WriteToFile(output, appendLineEnding: true);
                OnLogEmitted(tag, lineText, logLevel);
            }

            if (!atEnd && current == '\r' && i + 1 < messageSpan.Length && messageSpan[i + 1] == '\n')
            {
                i++;
            }

            lineStart = i + 1;
        }

        if (logLines.Count > 0)
        {
            applicationLogBuffer.Append(logLines);
        }
    }

    /// <summary>
    /// Delegates the console logger call to the implementation of this logger. Return true if a log line was written, retuirn false otherwise.
    /// </summary>
    protected virtual bool PlatformLog(string tag, string message, LogLevel logLevel)
    {
        return false;
    }

    protected virtual void OnLogEmitted(string tag, string message, LogLevel logLevel)
    {
    }

    public void Exception(Exception ex)
    {
        Log(Tag, ex.ToString(), LogLevel.Error);

        OnException(ex);

        // Debugger.Break();
    }

    protected virtual void OnException(Exception ex)
    {

    }

    public void Property(string key, string value, string group = "")
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException($"'{nameof(key)}' cannot be null or whitespace.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{nameof(value)}' cannot be null or whitespace.", nameof(value));
        }

        var tag = Tags.PropertyTag;
        if (!string.IsNullOrEmpty(group))
        {
            tag = $"{tag}.{group.Trim()}";
        }

        Log(RemoveWhitespace(tag), $"{key.Trim()}='{value}'", LogLevel.Information);
    }


    private static string RemoveWhitespace(string @string)
    {
        if (string.IsNullOrEmpty(@string))
        {
            return @string;
        }

        return new string(@string.ToCharArray()
            .Where(c => !char.IsWhiteSpace(c))
            .ToArray());
    }

    public void Event(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException($"'{nameof(eventName)}' cannot be null or whitespace.", nameof(eventName));
        }

        Log(Tag, BuildEventMessage(eventName, properties), LogLevel.Information);

        OnEvent(eventName, properties);
    }

    protected virtual void OnEvent(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {

    }

    public void Event(string eventName, params LogEventParameter[] parameters)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException($"'{nameof(eventName)}' cannot be null or whitespace.", nameof(eventName));
        }

        Log(Tag, BuildEventMessage(eventName, parameters), LogLevel.Information);

        OnEvent(eventName, parameters);
    }

    public bool IsFormattingSuspended
    {
        get
        {
            lock (suspendLogFormattingScopeLock)
            {
                return suspendLogFormattingScope != null && suspendLogFormattingScope.IsSuspended;
            }
        }
    }

    public ISuspendLogFormattingScope SuspendFormatting()
    {
        var newScope = new SuspendLogFormattingScope();

        lock (suspendLogFormattingScopeLock)
        {
            suspendLogFormattingScope = newScope;
        }


        return newScope;
    }

    public void ResumeFormatting()
    {
        lock (suspendLogFormattingScopeLock)
        {
            suspendLogFormattingScope = null;
        }
    }

    protected virtual void OnEvent(string eventName, params LogEventParameter[] parameters)
    {
    }

    private static string BuildEventMessage(string eventName, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return eventName.Trim();
        }

        var parameters = properties
            .Select(pair => new LogEventParameter(pair.Key, pair.Value))
            .ToArray();
        return BuildEventMessage(eventName, parameters);
    }

    private static string BuildEventMessage(string eventName, IReadOnlyList<LogEventParameter> parameters)
    {
        var builder = new StringBuilder(eventName.Trim());
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var parameterName = parameter.Name;
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            builder.Append(' ');
            builder.Append(parameterName.Trim());
            builder.Append('=');
            builder.Append(SerializeEventValue(parameter.Value));
        }

        return builder.ToString();
    }

    private static string SerializeEventValue(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return JsonSerializer.Serialize(value?.ToString());
        }
    }
}
