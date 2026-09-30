using System.Text.Json;

namespace Ansight.Analytics;

public sealed class EventOutbox
{
    private const string DirectoryName = "posthog-outbox";
    private const int MaximumEventCount = 250;
    private static readonly TimeSpan maximumEventAge = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string directoryPath;

    public EventOutbox(string analyticsDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analyticsDirectoryPath);
        directoryPath = Path.Combine(analyticsDirectoryPath, DirectoryName);
    }

    public bool TryStore(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            Directory.CreateDirectory(directoryPath);
            Prune(DateTimeOffset.UtcNow, reserveEventCount: 1);
            var filePath = Path.Combine(directoryPath, $"{envelope.InsertId}.json");
            if (File.Exists(filePath))
            {
                return true;
            }

            var temporaryPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(envelope, jsonOptions));
                File.Move(temporaryPath, filePath, overwrite: false);
                TryRestrictFilePermissions(filePath);
                return true;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException)
        {
            return false;
        }
    }

    public IReadOnlyList<OutboxEntry> Load(int maximumCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);

        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return [];
            }

            Prune(DateTimeOffset.UtcNow, reserveEventCount: 0);
            var events = new List<OutboxEntry>();
            foreach (var path in Directory
                         .EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => File.GetLastWriteTimeUtc(path))
                         .Take(maximumCount))
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<EventEnvelope>(
                        File.ReadAllText(path),
                        jsonOptions);
                    if (envelope is null
                        || string.IsNullOrWhiteSpace(envelope.InsertId)
                        || string.IsNullOrWhiteSpace(envelope.EventName)
                        || string.IsNullOrWhiteSpace(envelope.DistinctId))
                    {
                        TryDelete(path);
                        continue;
                    }

                    events.Add(new OutboxEntry(path, envelope));
                }
                catch (Exception exception) when (exception is IOException
                                                   or UnauthorizedAccessException
                                                   or JsonException)
                {
                    TryDelete(path);
                }
            }

            return events;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Remove(string path)
        => TryDelete(path);

    public void RemoveDetailedEvents()
    {
        foreach (var item in Load(MaximumEventCount))
        {
            if (item.Envelope.Level == EventLevel.Detailed)
            {
                TryDelete(item.Path);
            }
        }
    }

    private void Prune(DateTimeOffset nowUtc, int reserveEventCount)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        var oldestAllowedUtc = nowUtc - maximumEventAge;
        var retained = new List<string>();
        foreach (var path in Directory
                     .EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => File.GetLastWriteTimeUtc(path)))
        {
            DateTimeOffset lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (lastWriteUtc < oldestAllowedUtc)
            {
                TryDelete(path);
            }
            else
            {
                retained.Add(path);
            }
        }

        var removeCount = Math.Max(0, retained.Count - Math.Max(0, MaximumEventCount - reserveEventCount));
        for (var index = 0; index < removeCount; index++)
        {
            TryDelete(retained[index]);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
        }
    }
}
