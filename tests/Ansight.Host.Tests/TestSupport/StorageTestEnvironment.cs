namespace Ansight.Host.Tests.TestSupport;

internal sealed class StorageTestEnvironment : IDisposable
{
    private readonly TestDirectory directory;

    public StorageTestEnvironment()
    {
        directory = TestDirectory.Create();
        ApplicationPaths = new DataToolApplicationPaths(directory.Path);
        SecureStorageFilePath = Path.Combine(directory.Path, "secure-storage.json");
        SecureStorageKeyFilePath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(
            SecureStorageFilePath);
        FileEncryptionKeyProvider.CreateKeyFile(SecureStorageKeyFilePath);
    }

    public string RootPath => directory.Path;

    public string SecureStorageFilePath { get; }
    public string SecureStorageKeyFilePath { get; }
    public DataToolApplicationPaths ApplicationPaths { get; }

    public void Dispose()
    {
        directory.Dispose();
    }
}
