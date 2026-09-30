namespace Ansight.Infrastructure.Security;

public sealed class MacOsKeychainEncryptedStorage : IEncryptedStorage
{
    private const string DefaultService = "Ansight.Cli";
    private const string VaultAccount = "credential-vault-v1";
    private readonly Lock gate = new();
    private readonly string service;

    public MacOsKeychainEncryptedStorage()
        : this(DefaultService)
    {
    }

    internal MacOsKeychainEncryptedStorage(string service)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("The macOS Keychain provider is available only on macOS.");
        }

        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(service));
        }

        this.service = service.Trim();
    }

    public string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (gate)
        {
            var values = ReadValues();
            return values.TryGetValue(key.Trim(), out var value)
                ? value
                : null;
        }
    }

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(key));
        }

        lock (gate)
        {
            var normalizedKey = key.Trim();
            var values = ReadValues();
            if (value is null)
            {
                if (values.Remove(normalizedKey))
                {
                    WriteValues(values);
                }

                return;
            }

            values[normalizedKey] = value;
            WriteValues(values);
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (gate)
        {
            var values = ReadValues();
            if (values.Remove(key.Trim()))
            {
                WriteValues(values);
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            RemoveValue(VaultAccount);
        }
    }

    internal static string? ReadLegacyValue(string service, string account)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(account))
        {
            return null;
        }

        return GetValue(service.Trim(), account.Trim());
    }

    private Dictionary<string, string> ReadValues()
    {
        var content = GetValue(service, VaultAccount);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
            return values is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(values, StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Ansight CLI Keychain vault is invalid.", exception);
        }
    }

    private void WriteValues(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            RemoveValue(VaultAccount);
            return;
        }

        var normalized = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            normalized[entry.Key] = entry.Value;
        }

        SetValue(VaultAccount, JsonSerializer.Serialize(normalized));
    }

    private static string? GetValue(string service, string account)
    {
        var query = CreateQuery(service, account);
        nint data = 0;
        try
        {
            MacOsKeychainNative.SetValue(
                query,
                MacOsKeychainNative.SecReturnData,
                MacOsKeychainNative.BooleanTrue);
            MacOsKeychainNative.SetValue(
                query,
                MacOsKeychainNative.SecMatchLimit,
                MacOsKeychainNative.SecMatchLimitOne);
            var status = MacOsKeychainNative.CopyMatching(query, out data);
            if (status == MacOsKeychainNative.ItemNotFound)
            {
                return null;
            }

            EnsureSuccess("read", status);
            return Encoding.UTF8.GetString(MacOsKeychainNative.ReadData(data));
        }
        finally
        {
            MacOsKeychainNative.Release(data);
            MacOsKeychainNative.Release(query);
        }
    }

    private void SetValue(string account, string value)
    {
        var query = CreateQuery(service, account);
        var update = MacOsKeychainNative.CreateDictionary();
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            MacOsKeychainNative.SetData(update, MacOsKeychainNative.SecValueData, bytes);
            var status = MacOsKeychainNative.Update(query, update);
            if (status == MacOsKeychainNative.ItemNotFound)
            {
                MacOsKeychainNative.SetData(query, MacOsKeychainNative.SecValueData, bytes);
                MacOsKeychainNative.SetValue(
                    query,
                    MacOsKeychainNative.SecAttrAccessible,
                    MacOsKeychainNative.SecAttrAccessibleAfterFirstUnlockThisDeviceOnly);
                status = MacOsKeychainNative.Add(query);
            }

            EnsureSuccess("write", status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            MacOsKeychainNative.Release(update);
            MacOsKeychainNative.Release(query);
        }
    }

    private void RemoveValue(string account)
    {
        var query = CreateQuery(service, account);
        try
        {
            var status = MacOsKeychainNative.Delete(query);
            if (status != MacOsKeychainNative.ItemNotFound)
            {
                EnsureSuccess("remove", status);
            }
        }
        finally
        {
            MacOsKeychainNative.Release(query);
        }
    }

    private static nint CreateQuery(string service, string account)
    {
        var query = MacOsKeychainNative.CreateDictionary();
        MacOsKeychainNative.SetValue(
            query,
            MacOsKeychainNative.SecClass,
            MacOsKeychainNative.SecClassGenericPassword);
        MacOsKeychainNative.SetString(query, MacOsKeychainNative.SecAttrService, service);
        MacOsKeychainNative.SetString(query, MacOsKeychainNative.SecAttrAccount, account);
        return query;
    }

    private static void EnsureSuccess(string operation, int status)
    {
        if (status != MacOsKeychainNative.Success)
        {
            throw new InvalidOperationException(
                $"Could not {operation} the Ansight macOS Keychain item (OSStatus {status}).");
        }
    }
}
