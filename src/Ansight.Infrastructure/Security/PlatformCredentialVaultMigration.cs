namespace Ansight.Infrastructure.Security;

public static class PlatformCredentialVaultMigration
{
    public static bool TryMigrate(string filePath, IEncryptedStorage destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var values = ReadValues(filePath);
            if (values is null)
            {
                return false;
            }

            foreach (var entry in values)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrEmpty(entry.Value))
                {
                    continue;
                }

                var existingValue = destination.Get(entry.Key);
                if (!string.IsNullOrEmpty(existingValue))
                {
                    // The active OS vault is authoritative. Legacy files may be stale,
                    // so a differing existing value is not overwritten.
                    continue;
                }

                destination.Set(entry.Key, entry.Value);
                if (!string.Equals(destination.Get(entry.Key), entry.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            DeleteSensitiveFile(filePath);
            var localKeyFilePath = FileEncryptionKeyProvider.ResolveDefaultKeyFilePath(filePath);
            if (File.Exists(localKeyFilePath))
            {
                DeleteSensitiveFile(localKeyFilePath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or CryptographicException
                                           or InvalidOperationException
                                           or InvalidDataException)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string>? ReadValues(string filePath)
    {
        var content = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(content);
        if (document.RootElement.TryGetProperty("schema", out var schema)
            && schema.ValueKind == JsonValueKind.String)
        {
            var encryptedStorage = new FileBackedEncryptedStorage(filePath);
            return encryptedStorage.CreateMigrationSnapshot();
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
        return values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
    }

    private static void DeleteSensitiveFile(string filePath)
    {
        var length = new FileInfo(filePath).Length;
        using (var stream = new FileStream(
                   filePath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            var zeroes = new byte[8192];
            long written = 0;
            while (written < length)
            {
                var count = (int)Math.Min(zeroes.Length, length - written);
                stream.Write(zeroes, 0, count);
                written += count;
            }

            stream.Flush(flushToDisk: true);
        }

        File.Delete(filePath);
    }
}
