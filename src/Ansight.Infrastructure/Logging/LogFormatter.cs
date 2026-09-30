using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ansight.Infrastructure.Logging;

public static class LogFormatter
{
    private const string dateTimeFormat = "MM-dd HH:mm:ss.fff";

    private static readonly IReadOnlyDictionary<LogLevel, string> Levels = new Dictionary<LogLevel, string>()
    {
        { LogLevel.Verbose    , "V" },
        { LogLevel.Debug      , "D" },
        { LogLevel.Information, "I" },
        { LogLevel.Warning    , "W" },
        { LogLevel.Error      , "E" },
        { LogLevel.Fatal      , "F" },
    };

    public static string Render(DateTime dateTime, string tag, string message, int processId, int threadId, LogLevel logLevel)
    {
        return Render(dateTime, tag, message.AsMemory(), processId, threadId, logLevel);
    }

    /// <summary>
    /// Renders the given parameters into a threadtime formatted log entry.
    /// </summary>
    public static string Render(DateTime dateTime, string tag, ReadOnlyMemory<char> message, int processId, int threadId, LogLevel logLevel)
    {
        if (string.IsNullOrEmpty(tag))
        {
            throw new ArgumentException($"'{nameof(tag)}' cannot be null or empty.", nameof(tag));
        }

        var state = new RenderState(
            dateTime.ToString(dateTimeFormat),
            processId.ToString(CultureInfo.InvariantCulture),
            threadId.ToString(CultureInfo.InvariantCulture),
            Levels[logLevel],
            tag,
            message);

        var totalLength =
            state.DateTimeText.Length + 1 +
            state.ProcessIdText.Length + 1 +
            state.ThreadIdText.Length + 1 +
            state.Level.Length + 1 +
            state.Tag.Length + 2 +
            state.Message.Length;

        return string.Create(totalLength, state, static (span, renderState) =>
        {
            var position = 0;

            renderState.DateTimeText.AsSpan().CopyTo(span.Slice(position));
            position += renderState.DateTimeText.Length;
            span[position++] = ' ';

            renderState.ProcessIdText.AsSpan().CopyTo(span.Slice(position));
            position += renderState.ProcessIdText.Length;
            span[position++] = ' ';

            renderState.ThreadIdText.AsSpan().CopyTo(span.Slice(position));
            position += renderState.ThreadIdText.Length;
            span[position++] = ' ';

            renderState.Level.AsSpan().CopyTo(span.Slice(position));
            position += renderState.Level.Length;
            span[position++] = ' ';

            renderState.Tag.AsSpan().CopyTo(span.Slice(position));
            position += renderState.Tag.Length;

            span[position++] = ':';
            span[position++] = ' ';

            if (renderState.Message.Length > 0)
            {
                renderState.Message.Span.CopyTo(span.Slice(position));
            }
        });
    }

    private readonly struct RenderState
    {
        public RenderState(
            string dateTimeText,
            string processIdText,
            string threadIdText,
            string level,
            string tag,
            ReadOnlyMemory<char> message)
        {
            DateTimeText = dateTimeText;
            ProcessIdText = processIdText;
            ThreadIdText = threadIdText;
            Level = level;
            Tag = tag;
            Message = message;
        }

        public string DateTimeText { get; }
        public string ProcessIdText { get; }
        public string ThreadIdText { get; }
        public string Level { get; }
        public string Tag { get; }
        public ReadOnlyMemory<char> Message { get; }
    }
}
