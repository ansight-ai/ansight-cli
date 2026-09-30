using System.Security.Cryptography;

namespace Ansight.Host.Tests.Unit.Identity;

public sealed class HostIdentityStoreTests
{
    [Fact]
    public void Current_CreatesAndPersistsIdentityWhenStorageIsEmpty()
    {
        using var environment = new TestSupport.TestEnvironment();
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);

        string hostId;
        string publicKeyBase64;
        string fingerprint;
        using (var store = new IdentityStore(environment.ApplicationPaths, storage))
        {
            hostId = store.Current.HostId;
            publicKeyBase64 = store.Current.PublicKeyBase64;
            fingerprint = store.Current.Fingerprint;

            Assert.False(string.IsNullOrWhiteSpace(store.Current.HostName));
            Assert.Equal(43, hostId.Length);
            Assert.Equal(43, fingerprint.Length);
            Assert.False(string.IsNullOrWhiteSpace(storage.Get("host-identity")));
        }

        using var reloadedStore = new IdentityStore(
            environment.ApplicationPaths,
            new FileBackedEncryptedStorage(environment.SecureStorageFilePath));

        Assert.Equal(hostId, reloadedStore.Current.HostId);
        Assert.Equal(publicKeyBase64, reloadedStore.Current.PublicKeyBase64);
        Assert.Equal(fingerprint, reloadedStore.Current.Fingerprint);
    }

    [Fact]
    public void Current_MigratesLegacyIdentityFileIntoEncryptedStorage()
    {
        using var environment = new TestSupport.TestEnvironment();
        var legacyPath = Path.Combine(environment.ApplicationPaths.ApplicationDataPath, "host-identity.json");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPkcs8 = key.ExportPkcs8PrivateKey();
        var legacyDocument = new IdentityDocument
        {
            HostName = "Legacy Host",
            PublicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            PrivateKeyPkcs8 = Convert.ToBase64String(privateKeyPkcs8)
        };
        File.WriteAllText(
            legacyPath,
            JsonSerializer.Serialize(
                legacyDocument,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));

        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);
        using var store = new IdentityStore(environment.ApplicationPaths, storage);

        Assert.Equal("Legacy Host", store.Current.HostName);
        Assert.False(File.Exists(legacyPath));
        Assert.False(string.IsNullOrWhiteSpace(storage.Get("host-identity")));
    }

    [Fact]
    public void Current_UsesRuntimeIdentityScopeWithoutReplacingProductionIdentity()
    {
        using var environment = new TestSupport.TestEnvironment();
        var storage = new FileBackedEncryptedStorage(environment.SecureStorageFilePath);

        using var productionStore = new IdentityStore(
            environment.ApplicationPaths,
            storage,
            new RuntimeOptions());
        using var developerStore = new IdentityStore(
            environment.ApplicationPaths,
            storage,
            new RuntimeOptions
            {
                HostIdentityStorageKey = "host-identity-developer",
                HostIdentityName = "Test Mac (Developer)"
            });

        Assert.NotEqual(productionStore.Current.HostId, developerStore.Current.HostId);
        Assert.Equal("Test Mac (Developer)", developerStore.Current.HostName);
        Assert.False(string.IsNullOrWhiteSpace(storage.Get("host-identity")));
        Assert.False(string.IsNullOrWhiteSpace(storage.Get("host-identity-developer")));
    }
}
