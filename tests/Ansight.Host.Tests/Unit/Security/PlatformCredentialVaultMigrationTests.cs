namespace Ansight.Host.Tests.Unit.Security;

public sealed class PlatformCredentialVaultMigrationTests
{
    [Fact]
    public void TryMigrateCopiesAndVerifiesPlaintextBeforeDeletingSource()
    {
        using var environment = new TestSupport.TestEnvironment();
        File.WriteAllText(
            environment.SecureStorageFilePath,
            "{\"legacy-account\":\"refresh-token\",\"test-secret\":\"password\"}");
        var destination = new InMemoryEncryptedStorage();

        var migrated = PlatformCredentialVaultMigration.TryMigrate(
            environment.SecureStorageFilePath,
            destination);

        Assert.True(migrated);
        Assert.Equal("refresh-token", destination.Get("legacy-account"));
        Assert.Equal("password", destination.Get("test-secret"));
        Assert.False(File.Exists(environment.SecureStorageFilePath));
        Assert.False(File.Exists(environment.SecureStorageKeyFilePath));
    }

    [Fact]
    public void TryMigrateCopiesExistingAesVaultBeforeDeletingSourceAndLocalKey()
    {
        using var environment = new TestSupport.TestEnvironment();
        var source = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        source.Set("example", "encrypted-value");
        var destination = new InMemoryEncryptedStorage();

        var migrated = PlatformCredentialVaultMigration.TryMigrate(
            environment.SecureStorageFilePath,
            destination);

        Assert.True(migrated);
        Assert.Equal("encrypted-value", destination.Get("example"));
        Assert.False(File.Exists(environment.SecureStorageFilePath));
        Assert.False(File.Exists(environment.SecureStorageKeyFilePath));
    }

    [Fact]
    public void TryMigratePreservesExistingVaultValueAndCopiesMissingValues()
    {
        using var environment = new TestSupport.TestEnvironment();
        File.WriteAllText(
            environment.SecureStorageFilePath,
            "{\"existing\":\"stale-file-value\",\"missing\":\"legacy-value\"}");
        var destination = new InMemoryEncryptedStorage();
        destination.Set("existing", "current-vault-value");

        var migrated = PlatformCredentialVaultMigration.TryMigrate(
            environment.SecureStorageFilePath,
            destination);

        Assert.True(migrated);
        Assert.Equal("current-vault-value", destination.Get("existing"));
        Assert.Equal("legacy-value", destination.Get("missing"));
        Assert.False(File.Exists(environment.SecureStorageFilePath));
    }

    [Fact]
    public void TryMigrateLeavesSourceIntactWhenDestinationCannotVerifyValue()
    {
        using var environment = new TestSupport.TestEnvironment();
        const string sourceContent = "{\"legacy-account\":\"refresh-token\"}";
        File.WriteAllText(environment.SecureStorageFilePath, sourceContent);

        var migrated = PlatformCredentialVaultMigration.TryMigrate(
            environment.SecureStorageFilePath,
            new NonPersistingEncryptedStorage());

        Assert.False(migrated);
        Assert.True(File.Exists(environment.SecureStorageFilePath));
        Assert.Equal(sourceContent, File.ReadAllText(environment.SecureStorageFilePath));
    }

    [Fact]
    public void TryMigrateLeavesSourceIntactWhenDestinationThrows()
    {
        using var environment = new TestSupport.TestEnvironment();
        const string sourceContent = "{\"legacy-account\":\"refresh-token\"}";
        File.WriteAllText(environment.SecureStorageFilePath, sourceContent);

        var migrated = PlatformCredentialVaultMigration.TryMigrate(
            environment.SecureStorageFilePath,
            new ThrowingEncryptedStorage());

        Assert.False(migrated);
        Assert.True(File.Exists(environment.SecureStorageFilePath));
        Assert.Equal(sourceContent, File.ReadAllText(environment.SecureStorageFilePath));
    }

    private sealed class NonPersistingEncryptedStorage : IEncryptedStorage
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }

        public void Remove(string key)
        {
        }

        public void Clear()
        {
        }
    }

    private sealed class ThrowingEncryptedStorage : IEncryptedStorage
    {
        public string? Get(string key) => throw new InvalidOperationException("Credential vault unavailable.");

        public void Set(string key, string? value) => throw new InvalidOperationException("Credential vault unavailable.");

        public void Remove(string key) => throw new InvalidOperationException("Credential vault unavailable.");

        public void Clear() => throw new InvalidOperationException("Credential vault unavailable.");
    }
}
