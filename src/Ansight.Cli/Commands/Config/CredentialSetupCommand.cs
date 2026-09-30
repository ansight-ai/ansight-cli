using System.Security.Cryptography;
using Ansight.Infrastructure.Security;

namespace Ansight.Cli.Commands.Config;

internal static class CredentialSetupCommand
{
    public static int Run(CliArguments arguments, CliOutput output, TextReader? input, bool interactive)
    {
        arguments.EnsurePositionalCount(2, "ansight config credentials [--key-file <path>] [--non-interactive]");
        var options = CliRuntime.ResolveOptions(arguments);
        var settings = new LocalSettingsStore(options.DataDirectory);
        var saved = settings.Credentials;
        var requestedKey = arguments.GetOption("key-file");
        if (arguments.HasFlag("key-file") && string.IsNullOrWhiteSpace(requestedKey))
            throw new CliUsageException("--key-file requires an existing externally protected key file.");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FileEncryptionKeyProvider.MasterKeyEnvironmentVariable)))
            throw new CliUsageException("ANSIGHT_SECRET_MASTER_KEY overrides saved key references. Unset it before configuring persistent credentials, or use doctor --credentials-only for environment-managed CI credentials.");
        var keyFile = requestedKey ?? saved?.KeyFile ?? options.SecureStorageKeyFilePath;
        if (requestedKey is not null && saved is not null
            && !string.Equals(Path.GetFullPath(requestedKey), saved.KeyFile, StringComparison.Ordinal))
            throw new CliUsageException("Credential configuration already exists. Setup preserves its key and provider; use a deliberate credential migration to change them.");

        string message = "No persistent credential provider is configured.";
        CredentialSettings? candidate = null;
        if (keyFile is null && (saved is null || saved.Provider == "linux-secret-service")
            && OperatingSystem.IsLinux()
            && LinuxSecretServiceEncryptedStorage.TryProbe(true, out message))
        {
            candidate = new CredentialSettings("linux-secret-service", null, null);
        }
        if (candidate is null && saved is null && keyFile is null && interactive && !arguments.HasFlag("non-interactive") && !arguments.IsJson)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("Provide a separately protected, persistent key file containing a base64-encoded 32-byte key (mode 0400 or 0600). Setup will not generate, copy, or overwrite it.");
            Console.Error.Write("External key file path (blank to cancel): ");
            keyFile = input?.ReadLine()?.Trim();
        }
        if (candidate is null && !string.IsNullOrWhiteSpace(keyFile))
        {
            keyFile = Path.GetFullPath(keyFile);
            var store = options.SecureStorageFilePath ?? Path.Combine(options.DataDirectory, "data", "secure-storage.json");
            store = Path.GetFullPath(store);
            ValidateExternalKeyLocation(keyFile, store);
            var key = FileEncryptionKeyProvider.ReadKeyFile(keyFile);
            try
            {
                // Verify the key against existing ciphertext before saving any settings.
                if (File.Exists(store))
                    _ = FileBackedEncryptedStorage.OpenReadOnly(store, key);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            candidate = new CredentialSettings("protected-file", keyFile, options.SecureStorageFilePath is null ? null : store);
            message = "External key validated. The CLI and user service will read this same key reference; the key must remain available after reboot.";
        }
        if (candidate is null)
        {
            output.Write(new { Schema = "ansight.credential-setup/v1", IsReady = false, Message = message },
                () => message + "\nRun: ansight config credentials --key-file <absolute-path> --non-interactive");
            return CliExitCodes.CapabilityUnavailable;
        }
        if (saved is not null && candidate != saved)
            throw new CliUsageException("Setup will not change the saved credential provider, key, or store. Migrate credentials explicitly before changing configuration.");
        settings.SetCredentials(candidate);
        output.Write(new { Schema = "ansight.credential-setup/v1", IsReady = true, candidate.Provider, candidate.KeyFile, settings.SettingsPath, Message = message },
            () => $"Credentials: ready ({candidate.Provider})\n{message}\nSettings: {settings.SettingsPath}");
        return CliExitCodes.Success;
    }

    internal static void ValidateExternalKeyLocation(string keyFile, string store)
    {
        var keyInfo = new FileInfo(keyFile);
        var resolvedKey = keyInfo.ResolveLinkTarget(true)?.FullName ?? keyInfo.FullName;
        var storeDirectory = Path.GetDirectoryName(Path.GetFullPath(store))!;
        var directoryInfo = new DirectoryInfo(storeDirectory);
        var resolvedDirectory = directoryInfo.Exists
            ? directoryInfo.ResolveLinkTarget(true)?.FullName ?? directoryInfo.FullName
            : directoryInfo.FullName;
        if (string.Equals(Path.GetDirectoryName(resolvedKey), resolvedDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CliUsageException("Keep the externally protected key outside the encrypted store directory; setup will not accept a sibling master key.");
    }
}
