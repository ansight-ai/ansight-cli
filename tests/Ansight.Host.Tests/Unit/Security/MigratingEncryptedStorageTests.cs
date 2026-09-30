namespace Ansight.Host.Tests.Unit.Security;

public sealed class MigratingEncryptedStorageTests
{
    [Fact]
    public void GetCopiesLegacyValueIntoPrimaryStorage()
    {
        var primary = new InMemoryEncryptedStorage();
        var fallback = new InMemoryEncryptedStorage();
        fallback.Set("profile", "legacy");
        var storage = new MigratingEncryptedStorage(primary, fallback);

        Assert.Equal("legacy", storage.Get("profile"));
        Assert.Equal("legacy", primary.Get("profile"));
        Assert.Null(fallback.Get("profile"));
    }

    [Fact]
    public void SetWritesPrimaryAndClearsLegacyValue()
    {
        var primary = new InMemoryEncryptedStorage();
        var fallback = new InMemoryEncryptedStorage();
        fallback.Set("profile", "legacy");
        var storage = new MigratingEncryptedStorage(primary, fallback);

        storage.Set("profile", "shared");

        Assert.Equal("shared", primary.Get("profile"));
        Assert.Null(fallback.Get("profile"));
    }

    [Fact]
    public void GetPrefersPrimaryValue()
    {
        var primary = new InMemoryEncryptedStorage();
        var fallback = new InMemoryEncryptedStorage();
        primary.Set("profile", "shared");
        fallback.Set("profile", "legacy");
        var storage = new MigratingEncryptedStorage(primary, fallback);

        Assert.Equal("shared", storage.Get("profile"));
    }

    [Fact]
    public void RemoveClearsPrimaryAndFallbackValues()
    {
        var primary = new InMemoryEncryptedStorage();
        var fallback = new InMemoryEncryptedStorage();
        primary.Set("profile", "shared");
        fallback.Set("profile", "legacy");
        var storage = new MigratingEncryptedStorage(primary, fallback);

        storage.Remove("profile");

        Assert.Null(primary.Get("profile"));
        Assert.Null(fallback.Get("profile"));
        Assert.Null(storage.Get("profile"));
    }
}
