using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace Ansight.Infrastructure.Logging;

public class LogFileWriter : IDisposable
{
    private readonly struct LogFileEntry
    {
        public LogFileEntry(string content, bool appendLineEnding)
        {
            Content = content;
            AppendLineEnding = appendLineEnding;
        }

        public string Content { get; }
        public bool AppendLineEnding { get; }
    }

    private readonly ConcurrentQueue<LogFileEntry> outputQueue;
    private readonly Thread loggerThread;
    private readonly AutoResetEvent queueEvent;
    private readonly StreamWriter streamWriter;
    private readonly FileStream fileStream;

    private readonly object closedLock = new object();
    private bool closed = false;

    bool Closed
    {
        get
        {
            lock (closedLock)
            {
                return closed;
            }
        }
        set
        {
            lock (closedLock)
            {
                closed = value;
            }
        }
    }

    public string LogFilePath { get; }

    public LogFileWriter(string logFilePath)
    {
        if (string.IsNullOrEmpty(logFilePath))
        {
            throw new ArgumentException($"'{nameof(logFilePath)}' cannot be null or empty.", nameof(logFilePath));
        }

        LogFilePath = logFilePath;

        fileStream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
        streamWriter = new StreamWriter(fileStream)
        {
            AutoFlush = false
        };

        outputQueue = new ConcurrentQueue<LogFileEntry>();
        queueEvent = new AutoResetEvent(false);
        loggerThread = new Thread(LoggerWorker)
        {
            IsBackground = true,
            Name = "LogFileWriter"
        };
        loggerThread.Start();
    }

    public void Dispose()
    {
        if (Closed)
        {
            return;
        }

        Closed = true;
        queueEvent.Set();
        loggerThread.Join();

        FlushQueue();
        streamWriter.Dispose();
        fileStream.Dispose();
        queueEvent.Dispose();
    }

    public void WriteToFile(string output, bool appendLineEnding = false)
    {
        if (Closed)
        {
            throw new ObjectDisposedException(nameof(LogFileWriter));
        }

        if (output is null)
        {
            return;
        }

        outputQueue.Enqueue(new LogFileEntry(output, appendLineEnding));
        queueEvent.Set();
    }

    private void LoggerWorker()
    {
        while (true)
        {
            queueEvent.WaitOne();
            FlushQueue();

            if (Closed && outputQueue.IsEmpty)
            {
                break;
            }
        }
    }

    private void FlushQueue()
    {
        while (outputQueue.TryDequeue(out var entry))
        {
            if (entry.AppendLineEnding)
            {
                streamWriter.WriteLine(entry.Content);
            }
            else
            {
                streamWriter.Write(entry.Content);
            }
        }

        streamWriter.Flush();
    }
}
