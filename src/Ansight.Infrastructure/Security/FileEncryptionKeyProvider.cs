namespace Ansight.Infrastructure.Security;

public static class FileEncryptionKeyProvider
{
    public const string MasterKeyEnvironmentVariable = "ANSIGHT_SECRET_MASTER_KEY";
    public const string KeyFileEnvironmentVariable = "ANSIGHT_SECRET_KEY_FILE";
    public const string SystemdCredentialsDirectoryEnvironmentVariable = "CREDENTIALS_DIRECTORY";
    public const string SystemdCredentialFileName = "ansight-secret-master-key";

    private const int KeySizeInBytes = 32;

    public static byte[] Resolve(
        string encryptedStorageFilePath,
        string? keyFilePathOverride = null)
    {
        if (string.IsNullOrWhiteSpace(encryptedStorageFilePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(encryptedStorageFilePath));
        }

        var environmentKey = Environment.GetEnvironmentVariable(MasterKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return DecodeKey(environmentKey, MasterKeyEnvironmentVariable);
        }

        var configuredKeyFilePath = FirstNonEmpty(
            keyFilePathOverride,
            Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable));
        if (configuredKeyFilePath is not null)
        {
            return ReadKeyFile(Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configuredKeyFilePath)));
        }

        var credentialsDirectory = Environment.GetEnvironmentVariable(
            SystemdCredentialsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(credentialsDirectory))
        {
            var credentialPath = Path.Combine(
                credentialsDirectory.Trim(),
                SystemdCredentialFileName);
            if (!File.Exists(credentialPath))
            {
                throw new InvalidOperationException(
                    $"Systemd credentials directory was provided, but '{credentialPath}' does not exist.");
            }

            return ReadKeyFile(credentialPath);
        }

        throw new InvalidOperationException(
            "No protected master key is configured for the file-backed secret store. "
            + $"Set {KeyFileEnvironmentVariable}, {MasterKeyEnvironmentVariable}, or provide the systemd credential '{SystemdCredentialFileName}'.");
    }

    public static bool HasExternalKeySource(string? keyFilePathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MasterKeyEnvironmentVariable))
            || !string.IsNullOrWhiteSpace(keyFilePathOverride)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable)))
        {
            return true;
        }

        var credentialsDirectory = Environment.GetEnvironmentVariable(
            SystemdCredentialsDirectoryEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(credentialsDirectory);
    }

    public static string ResolveDefaultKeyFilePath(string encryptedStorageFilePath)
    {
        if (string.IsNullOrWhiteSpace(encryptedStorageFilePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(encryptedStorageFilePath));
        }

        var fullPath = Path.GetFullPath(encryptedStorageFilePath);
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(fullPath) + ".key";
        return Path.Combine(directory, fileName);
    }

    public static byte[] DecodeKey(string encodedKey, string sourceDescription)
    {
        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException($"{sourceDescription} is empty.");
        }

        try
        {
            var key = Convert.FromBase64String(encodedKey.Trim());
            if (key.Length != KeySizeInBytes)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new InvalidOperationException(
                    $"{sourceDescription} must contain a base64-encoded {KeySizeInBytes}-byte key.");
            }

            return key;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{sourceDescription} must contain a base64-encoded {KeySizeInBytes}-byte key.",
                exception);
        }
    }

    public static void CreateKeyFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(path));
        }

        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var key = RandomNumberGenerator.GetBytes(KeySizeInBytes);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(Convert.ToBase64String(key));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] ReadKeyFile(string path)
    {
        try
        {
            EnsureSafePermissions(path);
            return DecodeKey(File.ReadAllText(path), $"Secret key file '{path}'");
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Could not read secret key file '{path}'.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"Could not read secret key file '{path}'.", exception);
        }
    }

    private static void EnsureSafePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        var unsafeBits = mode & (UnixFileMode.GroupRead
                                 | UnixFileMode.GroupWrite
                                 | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherRead
                                 | UnixFileMode.OtherWrite
                                 | UnixFileMode.OtherExecute);
        if (unsafeBits != 0)
        {
            throw new InvalidOperationException(
                $"Secret key file '{path}' has unsafe Unix permissions ({mode}); use owner-only mode 0400 or 0600.");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
