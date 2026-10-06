namespace Ansight.Infrastructure;

public class DataToolApplicationPaths : IApplicationPaths
{
    public DataToolApplicationPaths(string baseFolderPath)
    {
        if (string.IsNullOrWhiteSpace(baseFolderPath))
        {
            throw new ArgumentException($"'{nameof(baseFolderPath)}' cannot be null or whitespace.", nameof(baseFolderPath));
        }

        BaseFolderPath = baseFolderPath;

        PrivateStorageDirectory.Ensure(BaseFolderPath);
        PrivateStorageDirectory.Ensure(ApplicationDataPath);
        AppDefinitionStorage.MigrateLegacyFiles(ApplicationDataPath);
        PrivateStorageDirectory.Ensure(ApplicationLogsPath);
        PrivateStorageDirectory.Ensure(ApplicationTempPath);
    }

    public string BaseFolderPath { get; }

    public string ApplicationDataPath => Path.Combine(BaseFolderPath, "data");

    public string ApplicationLogsPath => Path.Combine(BaseFolderPath, "logs");

    public string ApplicationTempPath => Path.Combine(BaseFolderPath, "temp");

}
