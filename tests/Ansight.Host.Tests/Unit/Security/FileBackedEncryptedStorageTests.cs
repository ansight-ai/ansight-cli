using System.Security.Cryptography;

namespace Ansight.Host.Tests.Unit.Security;

public sealed class FileBackedEncryptedStorageTests
{
    [Fact]
    public void SeparateInstances_SeeLatestValuesFromSharedBackingFile()
    {
        using var environment = new TestSupport.TestEnvironment();
        var first = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        var second = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);

        first.Set("first", "alpha");
        Assert.Equal("alpha", second.Get("first"));

        second.Set("second", "beta");
        Assert.Equal("beta", first.Get("second"));

        first.Remove("second");
        Assert.Null(second.Get("second"));
    }

    [Fact]
    public void PersistedStoreDoesNotContainKeysOrValuesInPlaintext()
    {
        using var environment = new TestSupport.TestEnvironment();
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);

        storage.Set("service-password", "extremely-secret-value");

        var content = File.ReadAllText(environment.SecureStorageFilePath);
        Assert.Contains("ansight.secure-storage/v2", content, StringComparison.Ordinal);
        Assert.DoesNotContain("service-password", content, StringComparison.Ordinal);
        Assert.DoesNotContain("extremely-secret-value", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorMigratesLegacyPlaintextDictionaryInPlace()
    {
        using var environment = new TestSupport.TestEnvironment();
        File.WriteAllText(
            environment.SecureStorageFilePath,
            "{\"legacy-key\":\"legacy-secret\"}");

        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);

        Assert.Equal("legacy-secret", storage.Get("legacy-key"));
        var content = File.ReadAllText(environment.SecureStorageFilePath);
        Assert.Contains("ansight.secure-storage/v2", content, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-secret", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsStoreEncryptedWithDifferentKey()
    {
        using var environment = new TestSupport.TestEnvironment();
        File.Delete(environment.SecureStorageFilePath);
        var firstKey = RandomNumberGenerator.GetBytes(32);
        var secondKey = RandomNumberGenerator.GetBytes(32);
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath, firstKey);
        storage.Set("key", "value");

        var exception = Assert.ThrowsAny<CryptographicException>(() =>
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath, secondKey));

        Assert.Contains("incorrect", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
