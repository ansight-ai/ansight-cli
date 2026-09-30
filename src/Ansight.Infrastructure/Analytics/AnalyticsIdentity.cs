using System.Security.Cryptography;
using System.Text;

namespace Ansight.Analytics;

public static class AnalyticsIdentity
{
    public static string LoadOrCreate(string analyticsDirectoryPath)
    {
        var path = Path.GetFullPath(Path.Combine(analyticsDirectoryPath, "posthog.distinct-id"));
        var existing = ReadIdentity(path);
        if (existing is not null) return existing;

        // File.Move(overwrite: false) alone does not serialize competing creators
        // on every platform. Keep identity creation shared across CLI/host processes.
        var lockName = $"ansight-analytics-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))}";
        using var mutex = new Mutex(false, lockName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired) throw new IOException("Analytics identity is being initialized by another process.");
            existing = ReadIdentity(path);
            if (existing is not null) return existing;

            Directory.CreateDirectory(analyticsDirectoryPath);
            var identity = $"cli_anon_{Guid.NewGuid():N}";
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, identity);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporaryPath, path, overwrite: true);
                return identity;
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static string? ReadIdentity(string path)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
