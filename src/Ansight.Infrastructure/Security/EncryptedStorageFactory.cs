namespace Ansight.Infrastructure.Security;

public static class EncryptedStorageFactory
{
    private const string DefaultSecureStorageFileName = "secure-storage.json";

    /// <summary>Opens an existing vault for diagnostics without credential migration, key creation or file writes.</summary>
    public static IEncryptedStorage CreateReadOnly(string defaultFilePath, string? fileOverride = null, string? keyOverride = null)
    {
        var provider = ResolveDefaultProviderName(fileOverride, keyOverride);
        if (provider == "macos-keychain") return new MacOsKeychainEncryptedStorage();
        if (provider == "linux-secret-service" && LinuxSecretServiceEncryptedStorage.TryResolveExecutable(out var executable))
            return new LinuxSecretServiceEncryptedStorage(executable);
        if (provider is "protected-file" or "windows-dpapi")
        {
            var path = string.IsNullOrWhiteSpace(fileOverride) ? defaultFilePath : Path.GetFullPath(fileOverride);
            if (!File.Exists(path)) throw new FileNotFoundException("The secret store has not been created.");
            var key = provider == "windows-dpapi" ? WindowsDpapiKeyProvider.Resolve(path, createIfMissing: false)
                : FileEncryptionKeyProvider.Resolve(path, keyOverride);
            try { return FileBackedEncryptedStorage.OpenReadOnly(path, key); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        throw new InvalidOperationException("The credential provider is unavailable.");
    }

    public static IEncryptedStorage CreateDefault(
        IApplicationPaths applicationPaths,
        string? secureStorageFilePathOverride = null,
        string? secureStorageKeyFilePathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        var fileBackedStoragePath = ResolveFileBackedStoragePath(applicationPaths, secureStorageFilePathOverride);
        if (!string.IsNullOrWhiteSpace(secureStorageFilePathOverride)
            || FileEncryptionKeyProvider.HasExternalKeySource(secureStorageKeyFilePathOverride))
        {
            return CreateProtectedFileStorage(
                fileBackedStoragePath,
                secureStorageKeyFilePathOverride);
        }

        if (OperatingSystem.IsMacOS())
        {
            IEncryptedStorage storage = new MacOsKeychainEncryptedStorage();
            MigratePlatformCredentialVault(fileBackedStoragePath, storage);
            return storage;
        }

        if (OperatingSystem.IsWindows())
        {
            var encryptionKey = WindowsDpapiKeyProvider.Resolve(fileBackedStoragePath);
            try
            {
                return new FileBackedEncryptedStorage(fileBackedStoragePath, encryptionKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }

        if (LinuxSecretServiceEncryptedStorage.TryResolveExecutable(out var secretToolPath))
        {
            var storage = new LinuxSecretServiceEncryptedStorage(secretToolPath);
            storage.MigrateLegacyEntries();
            MigratePlatformCredentialVault(fileBackedStoragePath, storage);
            return storage;
        }

        throw new InvalidOperationException(
            "No OS credential vault is available. Linux headless hosts must provide "
            + $"{FileEncryptionKeyProvider.KeyFileEnvironmentVariable}, "
            + $"{FileEncryptionKeyProvider.MasterKeyEnvironmentVariable}, or the systemd credential "
            + $"'{FileEncryptionKeyProvider.SystemdCredentialFileName}'.");
    }

    public static string ResolveDefaultProviderName(
        string? secureStorageFilePathOverride = null,
        string? secureStorageKeyFilePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(secureStorageFilePathOverride)
            || FileEncryptionKeyProvider.HasExternalKeySource(secureStorageKeyFilePathOverride))
        {
            return "protected-file";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos-keychain";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows-dpapi";
        }

        return LinuxSecretServiceEncryptedStorage.TryResolveExecutable(out _)
            ? "linux-secret-service"
            : "unavailable";
    }

    private static IEncryptedStorage CreateProtectedFileStorage(
        string fileBackedStoragePath,
        string? secureStorageKeyFilePathOverride)
    {
        var encryptionKey = FileEncryptionKeyProvider.Resolve(
            fileBackedStoragePath,
            secureStorageKeyFilePathOverride);
        try
        {
            return new FileBackedEncryptedStorage(fileBackedStoragePath, encryptionKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
        }
    }

    private static void MigratePlatformCredentialVault(
        string fileBackedStoragePath,
        IEncryptedStorage destination)
    {
        if (File.Exists(fileBackedStoragePath)
            && !PlatformCredentialVaultMigration.TryMigrate(fileBackedStoragePath, destination))
        {
            throw new InvalidOperationException(
                $"Could not verify migration of legacy credentials from '{fileBackedStoragePath}' into the OS credential vault. The source was left intact.");
        }
    }

    public static string ResolveFileBackedStoragePath(
        IApplicationPaths applicationPaths,
        string? secureStorageFilePathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        return string.IsNullOrWhiteSpace(secureStorageFilePathOverride)
            ? Path.Combine(applicationPaths.ApplicationDataPath, DefaultSecureStorageFileName)
            : Path.GetFullPath(secureStorageFilePathOverride.Trim());
    }
}
