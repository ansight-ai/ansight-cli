namespace Ansight.Host.Devices;

public sealed class DeviceUsageStore
{
    private readonly string directory;

    public DeviceUsageStore(string dataDirectory)
    {
        directory = Path.GetFullPath(Path.Combine(dataDirectory, "device-usage"));
    }

    public string? Warning { get; private set; }

    public DeviceUsageHistory Read(string platform, string identifier)
    {
        return Update(platform, identifier, new DeviceUsageHistory());
    }

    public DeviceUsageHistory Update(string platform, string identifier, DeviceUsageHistory observed)
    {
        var key = platform.Trim().ToLowerInvariant() + ":" + identifier.Trim();

        if (platform == DevicePlatforms.Ios)
        {
            key = key.ToLowerInvariant();
        }

        var path = Path.Combine(directory, Hash(key) + ".json");
        using var mutex = new Mutex(false, "ansight-device-usage-" + Hash(path));
        var acquired = false;

        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new IOException("Device usage history is busy.");
            }

            var existing = File.Exists(path)
                ? JsonSerializer.Deserialize<DeviceUsageHistory>(File.ReadAllText(path)) ?? new DeviceUsageHistory()
                : new DeviceUsageHistory();
            var merged = new DeviceUsageHistory(
                Latest(existing.LastBootedUtc, observed.LastBootedUtc),
                Latest(existing.LastUsedUtc, observed.LastUsedUtc));

            if (merged != existing)
            {
                Directory.CreateDirectory(directory);
                var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(merged));
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    File.Delete(temporaryPath);
                }
            }

            Warning = null;

            return merged;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = $"Device usage history could not be read or saved: {exception.Message}";

            return observed;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second)
    {
        var secondIsNewer = !first.HasValue || second > first;

        return secondIsNewer ? second?.ToUniversalTime() : first?.ToUniversalTime();
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
