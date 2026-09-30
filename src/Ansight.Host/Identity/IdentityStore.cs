namespace Ansight.Host.Identity;

using Ansight.Host;
using System.ComponentModel.Composition;

[Export(typeof(IIdentityStore))]
[PartCreationPolicy(CreationPolicy.Shared)]
internal sealed class IdentityStore : IIdentityStore
{
    private const string DefaultStorageKey = "host-identity";

    public RuntimeIdentity Current { get; }

    internal IdentityStore(
        IApplicationPaths applicationPaths,
        Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage)
        : this(applicationPaths, encryptedStorage, new RuntimeOptions())
    {
    }

    [ImportingConstructor]
    public IdentityStore(
        IApplicationPaths applicationPaths,
        Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage,
        RuntimeOptions runtimeOptions)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeOptions.HostIdentityStorageKey);

        var identityFilePath = Path.Combine(applicationPaths.ApplicationDataPath, "host-identity.json");
        Current = LoadOrCreate(
            encryptedStorage,
            identityFilePath,
            runtimeOptions.HostIdentityStorageKey,
            runtimeOptions.HostIdentityName);
    }

    public void Dispose()
    {
        Current.Dispose();
    }

    private static RuntimeIdentity LoadOrCreate(
        Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage,
        string legacyPath,
        string storageKey,
        string? hostIdentityName)
    {
        ArgumentNullException.ThrowIfNull(encryptedStorage);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);

        var storedJson = encryptedStorage.Get(storageKey);
        if (TryCreateIdentity(storedJson, out var current))
        {
            return current;
        }

        if (string.Equals(storageKey, DefaultStorageKey, StringComparison.Ordinal)
            && File.Exists(legacyPath))
        {
            try
            {
                var legacyJson = File.ReadAllText(legacyPath);
                if (TryCreateIdentity(legacyJson, out current))
                {
                    encryptedStorage.Set(storageKey, legacyJson);
                    File.Delete(legacyPath);
                    return current;
                }
            }
            catch
            {
                // Regenerate identity if the legacy file is corrupted.
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPkcs8 = key.ExportPkcs8PrivateKey();
        var hostName = string.IsNullOrWhiteSpace(hostIdentityName)
            ? Environment.MachineName
            : hostIdentityName.Trim();
        var identity = RuntimeIdentity.FromPrivateKey(hostName, privateKeyPkcs8);

        var document = new IdentityDocument
        {
            HostName = identity.HostName,
            PublicKeySpki = identity.PublicKeyBase64,
            PrivateKeyPkcs8 = Convert.ToBase64String(privateKeyPkcs8)
        };

        encryptedStorage.Set(storageKey, JsonSerializer.Serialize(document, JsonUtil.Pretty));
        return identity;
    }

    private static bool TryCreateIdentity(string? json, out RuntimeIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            identity = null!;
            return false;
        }

        try
        {
            var existing = JsonSerializer.Deserialize<IdentityDocument>(json, JsonUtil.Compact);
            if (existing is not null &&
                !string.IsNullOrWhiteSpace(existing.PrivateKeyPkcs8) &&
                !string.IsNullOrWhiteSpace(existing.HostName))
            {
                var privateKey = Convert.FromBase64String(existing.PrivateKeyPkcs8);
                identity = RuntimeIdentity.FromPrivateKey(existing.HostName, privateKey);
                return true;
            }
        }
        catch (Exception suppressedException)
        {
            System.Diagnostics.Trace.TraceWarning(suppressedException.ToString());
        }

        identity = null!;
        return false;
    }
}
