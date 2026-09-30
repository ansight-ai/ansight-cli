using Ansight.Host.Tests.TestSupport;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.Tests.Unit.Security;

public sealed class FileBackedEncryptedStoragePermissionsTests
{
    [Fact]
    public void PersistedStoreIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        var path = System.IO.Path.Combine(directory.Path, "secure-storage.json");
        var keyPath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(path);
        FileEncryptionKeyProvider.CreateKeyFile(keyPath);
        var storage = new FileBackedEncryptedStorage(path);

        storage.Set("test-key", "test-value");

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(keyPath));
    }
}
