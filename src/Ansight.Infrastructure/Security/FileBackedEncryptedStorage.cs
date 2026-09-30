namespace Ansight.Infrastructure.Security;

public sealed class FileBackedEncryptedStorage : IEncryptedStorage
{
    private const string Schema = "ansight.secure-storage/v2";
    private const string Algorithm = "AES-256-GCM";
    private const int NonceSizeInBytes = 12;
    private const int TagSizeInBytes = 16;
    private static readonly byte[] additionalAuthenticatedData = Encoding.UTF8.GetBytes(Schema);

    private readonly Lock gate = new();
    private readonly string filePath;
    private readonly byte[] encryptionKey;
    private readonly JsonSerializerOptions jsonOptions;
    private readonly bool readOnly;
    private Dictionary<string, string> values;

    public FileBackedEncryptedStorage(string filePath)
        : this(
            filePath,
            FileEncryptionKeyProvider.Resolve(
                filePath,
                FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(filePath)),
            clearSourceKey: true)
    {
    }

    public FileBackedEncryptedStorage(string filePath, byte[] encryptionKey)
        : this(filePath, encryptionKey, clearSourceKey: false)
    {
    }

    public static FileBackedEncryptedStorage OpenReadOnly(string filePath, byte[] encryptionKey)
        => new(filePath, encryptionKey, clearSourceKey: false, readOnly: true);

    private FileBackedEncryptedStorage(string filePath, byte[] encryptionKey, bool clearSourceKey, bool readOnly = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
            }

            ArgumentNullException.ThrowIfNull(encryptionKey);
            if (encryptionKey.Length != 32)
            {
                throw new ArgumentException("The encryption key must contain exactly 32 bytes.", nameof(encryptionKey));
            }

            this.filePath = Path.GetFullPath(filePath);
            this.readOnly = readOnly;
            this.encryptionKey = encryptionKey.ToArray();
            jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = true,
            };

            if (readOnly)
            {
                values = LoadFromDisk(out _);
                return;
            }

            var parent = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using var fileLock = AcquireFileLock();
            values = LoadFromDisk(out var requiresMigration);
            if (requiresMigration)
            {
                PersistToDisk(values);
            }

            TryRestrictFilePermissions(this.filePath);
        }
        finally
        {
            if (clearSourceKey && encryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }
        }
    }

    public string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (gate)
        {
            if (readOnly) return values.GetValueOrDefault(key);
            using var fileLock = AcquireFileLock();
            ReloadFromDisk();
            return values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Set(string key, string? value)
    {
        if (readOnly) throw new InvalidOperationException("This secret store is a read-only snapshot.");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(key));
        }

        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            ReloadFromDisk();
            if (value is null)
            {
                if (values.Remove(key))
                {
                    PersistToDisk(values);
                }

                return;
            }

            if (values.TryGetValue(key, out var current) && string.Equals(current, value, StringComparison.Ordinal))
            {
                return;
            }

            values[key] = value;
            PersistToDisk(values);
        }
    }

    public void Remove(string key)
    {
        if (readOnly) throw new InvalidOperationException("This secret store is a read-only snapshot.");
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            ReloadFromDisk();
            if (!values.Remove(key))
            {
                return;
            }

            PersistToDisk(values);
        }
    }

    public void Clear()
    {
        if (readOnly) throw new InvalidOperationException("This secret store is a read-only snapshot.");
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            ReloadFromDisk();
            if (values.Count == 0)
            {
                return;
            }

            values.Clear();
            PersistToDisk(values);
        }
    }

    internal IReadOnlyDictionary<string, string> CreateMigrationSnapshot()
    {
        lock (gate)
        {
            using var fileLock = AcquireFileLock();
            ReloadFromDisk();
            return new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
    }

    private void ReloadFromDisk()
    {
        values = LoadFromDisk(out var requiresMigration);
        if (requiresMigration)
        {
            PersistToDisk(values);
        }
    }

    private Dictionary<string, string> LoadFromDisk(out bool requiresMigration)
    {
        requiresMigration = false;
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var content = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Secret store '{filePath}' does not contain a JSON object.");
            }

            if (!document.RootElement.TryGetProperty("schema", out _))
            {
                var legacyValues = JsonSerializer.Deserialize<Dictionary<string, string>>(content, jsonOptions)
                                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
                requiresMigration = true;
                return new Dictionary<string, string>(legacyValues, StringComparer.Ordinal);
            }

            var envelope = JsonSerializer.Deserialize<EncryptedStorageEnvelope>(content, jsonOptions)
                           ?? throw new InvalidDataException($"Secret store '{filePath}' has an invalid envelope.");
            if (!string.Equals(envelope.Schema, Schema, StringComparison.Ordinal)
                || !string.Equals(envelope.Algorithm, Algorithm, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Secret store '{filePath}' uses unsupported schema '{envelope.Schema}' or algorithm '{envelope.Algorithm}'.");
            }

            return Decrypt(envelope);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Secret store '{filePath}' is not valid JSON.", exception);
        }
    }

    private Dictionary<string, string> Decrypt(EncryptedStorageEnvelope envelope)
    {
        try
        {
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            if (nonce.Length != NonceSizeInBytes || tag.Length != TagSizeInBytes)
            {
                throw new InvalidDataException($"Secret store '{filePath}' has invalid encryption metadata.");
            }

            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(encryptionKey, TagSizeInBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, additionalAuthenticatedData);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext, jsonOptions);
                return deserialized is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(deserialized, StringComparer.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Secret store '{filePath}' has invalid base64 encryption metadata.", exception);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new CryptographicException(
                $"Secret store '{filePath}' could not be authenticated. The configured master key is incorrect or the file was modified.",
                exception);
        }
    }

    private void PersistToDisk(Dictionary<string, string> currentValues)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(currentValues, jsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeInBytes];
        try
        {
            using var aes = new AesGcm(encryptionKey, TagSizeInBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, additionalAuthenticatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var envelope = new EncryptedStorageEnvelope(
            Schema,
            Algorithm,
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(ciphertext));
        var json = JsonSerializer.Serialize(envelope, jsonOptions);
        var tempPath = filePath + ".tmp";

        WriteOwnerOnlyFile(tempPath, json);
        TryRestrictFilePermissions(tempPath);
        File.Move(tempPath, filePath, overwrite: true);
        TryRestrictFilePermissions(filePath);
    }

    private static void WriteOwnerOnlyFile(string path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
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
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private FileStream AcquireFileLock()
    {
        var lockPath = filePath + ".lock";
        var startedUtc = DateTimeOffset.UtcNow;
        while (true)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.None
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                var stream = new FileStream(lockPath, options);
                TryRestrictFilePermissions(lockPath);
                return stream;
            }
            catch (IOException exception)
            {
                if (DateTimeOffset.UtcNow - startedUtc >= TimeSpan.FromSeconds(5))
                {
                    throw new IOException(
                        $"Timed out waiting for exclusive access to secret store '{filePath}'.",
                        exception);
                }

                Thread.Sleep(25);
            }
        }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or PlatformNotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Could not restrict secure-storage permissions for '{path}': {exception.Message}");
        }
    }
}
