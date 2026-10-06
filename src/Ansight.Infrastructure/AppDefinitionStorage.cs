namespace Ansight.Infrastructure;

public static class AppDefinitionStorage
{
    public static string GetDirectoryPath(string applicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataPath);
        return Path.Combine(applicationDataPath, "apps");
    }

    public static void MigrateLegacyFiles(string applicationDataPath)
    {
        var appsPath = GetDirectoryPath(applicationDataPath);
        PrivateStorageDirectory.Ensure(appsPath);

        foreach (var sourcePath in Directory.EnumerateFiles(
                     applicationDataPath, "*.ans.json", SearchOption.TopDirectoryOnly))
        {
            var destinationPath = Path.Combine(appsPath, Path.GetFileName(sourcePath));
            try
            {
                // Keep both files when a name already exists in the new location.
                File.Move(sourcePath, destinationPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A later launch can retry files that are locked or unavailable.
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in Directory.EnumerateFiles(appsPath, "*.ans.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The private parent directory still protects files whose modes cannot be changed.
                }
            }
        }
    }
}
