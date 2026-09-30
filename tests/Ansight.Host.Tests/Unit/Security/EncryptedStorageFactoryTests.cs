namespace Ansight.Host.Tests.Unit.Security;

public sealed class EncryptedStorageFactoryTests
{
    [Fact]
    public void ResolveDefaultProviderNameUsesNativeMacOsKeychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal("macos-keychain", EncryptedStorageFactory.ResolveDefaultProviderName());
    }

    [Fact]
    public void CreateDefault_WithFilePathOverride_UsesFileBackedStorage()
    {
        using var environment = new TestSupport.TestEnvironment();

        var storage = EncryptedStorageFactory.CreateDefault(
            environment.ApplicationPaths,
            environment.SecureStorageFilePath,
            environment.SecureStorageKeyFilePath);

        Assert.IsType<FileBackedEncryptedStorage>(storage);

        storage.Set("example", "value");
        Assert.True(File.Exists(environment.SecureStorageFilePath));
    }

    [Fact]
    public void ResolveFileBackedStoragePath_UsesApplicationDataPathByDefault()
    {
        using var environment = new TestSupport.TestEnvironment();

        var path = EncryptedStorageFactory.ResolveFileBackedStoragePath(environment.ApplicationPaths);

        Assert.Equal(
            Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "secure-storage.json"),
            path);
    }
}
