namespace Ansight.Host.Tests.Unit.Security;

public sealed class MacOsKeychainEncryptedStorageTests
{
    [Fact]
    public void SetGetRemoveAndClearUseNativeKeychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var service = $"Ansight.Host.Tests.{Guid.NewGuid():N}";
        var storage = new MacOsKeychainEncryptedStorage(service);
        try
        {
            storage.Set("first", "alpha");
            storage.Set("second", "beta");

            Assert.Equal("alpha", storage.Get("first"));
            Assert.Equal("beta", storage.Get("second"));
            Assert.Null(MacOsKeychainEncryptedStorage.ReadLegacyValue(service, "first"));
            Assert.NotNull(MacOsKeychainEncryptedStorage.ReadLegacyValue(service, "credential-vault-v1"));

            storage.Remove("first");
            Assert.Null(storage.Get("first"));

            storage.Clear();
            Assert.Null(storage.Get("second"));
        }
        finally
        {
            storage.Clear();
        }
    }
}
