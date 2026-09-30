using System;
using System.Collections.Generic;
using System.Threading;

namespace Ansight.Infrastructure.Logging;

public class MutableApplicationLogBuffer : IMutableApplicationLogBuffer
{
    public static readonly IMutableApplicationLogBuffer Instance = new MutableApplicationLogBuffer();

    const int defaultCapacity = 2500;

    private readonly object logsLock = new object();
    private readonly List<string> logs = new List<string>();
    private string[] logsSnapshot = Array.Empty<string>();

    private readonly object capacityLock = new object();
    private int capacity = defaultCapacity;

    public int Capacity
    {
        get
        {
            lock (capacityLock)
            {
                return capacity;
            }
        }
    }

    public IReadOnlyList<string> Logs => Volatile.Read(ref logsSnapshot);

    public event EventHandler<LogBufferChangedEventArgs>? OnLogsChanged;

    public void Append(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var lines = SplitContentIntoLines(content);
        if (lines.Count == 0)
        {
            return;
        }

        Append(lines);
    }

    public void Append(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> removedLines;
        var capacitySnapshot = Capacity;

        lock (logsLock)
        {
            logs.AddRange(lines);
            removedLines = EnforceCapacityLocked(capacitySnapshot);
            UpdateSnapshotLocked();
        }

        OnLogsChanged?.Invoke(this, new LogBufferChangedEventArgs(lines, removedLines));
    }

    private IReadOnlyList<string> EnforceCapacityLocked(int targetCapacity)
    {
        if (targetCapacity < 0)
        {
            var removed = logs.ToArray();
            logs.Clear();
            return removed;
        }

        if (logs.Count <= targetCapacity)
        {
            return Array.Empty<string>();
        }

        var overflow = logs.Count - targetCapacity;
        var removedLines = logs.GetRange(0, overflow);
        logs.RemoveRange(0, overflow);

        return removedLines;
    }

    public void Clear()
    {
        IReadOnlyList<string> removedLines;

        lock (logsLock)
        {
            if (logs.Count == 0)
            {
                return;
            }

            removedLines = logs.ToArray();
            logs.Clear();
            logsSnapshot = Array.Empty<string>();
        }

        OnLogsChanged?.Invoke(this, new LogBufferChangedEventArgs(Array.Empty<string>(), removedLines));
    }

    public void SetCapacity(int capacity)
    {
        lock (capacityLock)
        {
            this.capacity = capacity;
        }

        IReadOnlyList<string> removedLines;

        lock (logsLock)
        {
            removedLines = EnforceCapacityLocked(capacity);
            UpdateSnapshotLocked();
        }

        if (removedLines.Count > 0)
        {
            OnLogsChanged?.Invoke(this, new LogBufferChangedEventArgs(Array.Empty<string>(), removedLines));
        }
    }

    private void UpdateSnapshotLocked()
    {
        logsSnapshot = logs.ToArray();
    }

    private static List<string> SplitContentIntoLines(string content)
    {
        var result = new List<string>();
        var span = content.AsSpan();
        var lineStart = 0;

        for (var i = 0; i <= span.Length; i++)
        {
            var atEnd = i == span.Length;
            var current = atEnd ? '\n' : span[i];

            if (!atEnd && current != '\n' && current != '\r')
            {
                continue;
            }

            var lineLength = i - lineStart;
            var line = lineLength > 0 ? content.Substring(lineStart, lineLength) : string.Empty;
            result.Add(line);

            if (!atEnd && current == '\r' && i + 1 < span.Length && span[i + 1] == '\n')
            {
                i++;
            }

            lineStart = i + 1;
        }

        return result;
    }
}
