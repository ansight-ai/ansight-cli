using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ansight.Infrastructure.Security;

namespace Ansight.Host.SimulatorAgent.Secrets;

internal sealed partial class SecretStore
{
    private const string StoragePrefix = "simulator-agent.secrets.v1";
    private const string EnvironmentVersionId = "environment";
    private const int MaximumSecretValueCharacters = 16_384;
    private readonly IEncryptedStorage encryptedStorage;
    private readonly Func<string, string?> environmentVariableResolver;
    private readonly Lock gate = new();
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public SecretStore(IEncryptedStorage encryptedStorage)
        : this(encryptedStorage, Environment.GetEnvironmentVariable)
    {
    }

    internal SecretStore(
        IEncryptedStorage encryptedStorage,
        Func<string, string?> environmentVariableResolver)
    {
        this.encryptedStorage = encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage));
        this.environmentVariableResolver = environmentVariableResolver
            ?? throw new ArgumentNullException(nameof(environmentVariableResolver));
    }

    public IReadOnlyList<SimulatorAgentSecretMetadata> List(string appId)
    {
        var normalizedAppId = NormalizeAppId(appId);
        lock (gate)
        {
            return ReadIndex(normalizedAppId)
                .OrderBy(static secret => secret.Alias, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal IReadOnlyList<SimulatorAgentSecretMetadata> ListForDiagnostics(string appId)
    {
        lock (gate)
        {
            var source = encryptedStorage.Get(BuildIndexKey(NormalizeAppId(appId)));
            if (string.IsNullOrWhiteSpace(source)) return [];
            return JsonSerializer.Deserialize<List<SimulatorAgentSecretMetadata>>(source, jsonOptions)
                ?? throw new InvalidDataException("Secret metadata could not be decoded.");
        }
    }

    public SimulatorAgentSecretMetadata Set(string appId, string alias, string value)
    {
        var normalizedAppId = NormalizeAppId(appId);
        var normalizedAlias = NormalizeAlias(alias);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("The secret value cannot be empty.", nameof(value));
        }
        if (value.Length > MaximumSecretValueCharacters)
        {
            throw new ArgumentException(
                $"The secret value must be {MaximumSecretValueCharacters:N0} characters or fewer.",
                nameof(value));
        }

        lock (gate)
        {
            var index = ReadIndex(normalizedAppId).ToList();
            var valueKey = BuildValueKey(normalizedAppId, normalizedAlias);
            var previousValue = encryptedStorage.Get(valueKey);
            var metadata = new SimulatorAgentSecretMetadata(
                normalizedAlias,
                normalizedAppId,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow);
            encryptedStorage.Set(valueKey, value);
            index.RemoveAll(item => string.Equals(
                item.Alias,
                normalizedAlias,
                StringComparison.OrdinalIgnoreCase));
            index.Add(metadata);
            try
            {
                WriteIndex(normalizedAppId, index);
            }
            catch
            {
                encryptedStorage.Set(valueKey, previousValue);
                throw;
            }
            return metadata;
        }
    }

    public bool Remove(string appId, string alias)
    {
        var normalizedAppId = NormalizeAppId(appId);
        var normalizedAlias = NormalizeAlias(alias);
        lock (gate)
        {
            var index = ReadIndex(normalizedAppId).ToList();
            var previousIndex = index.ToArray();
            var removed = index.RemoveAll(item => string.Equals(
                item.Alias,
                normalizedAlias,
                StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                WriteIndex(normalizedAppId, index);
            }
            try
            {
                encryptedStorage.Remove(BuildValueKey(normalizedAppId, normalizedAlias));
            }
            catch
            {
                if (removed)
                {
                    WriteIndex(normalizedAppId, previousIndex);
                }
                throw;
            }
            return removed;
        }
    }

    public SecretAccess CreateRunAccess(
        string? appId,
        IReadOnlyList<string>? aliases,
        Func<string, string?>? secretResolver = null)
    {
        var normalizedAliases = NormalizeAliases(aliases);
        if (normalizedAliases.Count == 0)
        {
            return SecretAccess.Empty;
        }
        var normalizedAppId = NormalizeAppId(appId);
        lock (gate)
        {
            var index = ReadIndex(normalizedAppId);
            var metadata = new List<SimulatorAgentSecretMetadata>(normalizedAliases.Count);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in normalizedAliases)
            {
                var resolved = Resolve(index, normalizedAppId, alias, secretResolver);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"Required test secret '{alias}' is not configured for {normalizedAppId}.");
                }
                metadata.Add(resolved.Metadata);
                values[resolved.Metadata.Alias] = resolved.Value;
            }

            return new SecretAccess(
                metadata.ToDictionary(static item => item.Alias, StringComparer.OrdinalIgnoreCase),
                alias => values.GetValueOrDefault(alias));
        }
    }

    public SimulatorAgentSecretMetadata? GetMetadata(
        string? appId,
        string alias,
        Func<string, string?>? secretResolver = null)
    {
        var normalizedAppId = NormalizeAppId(appId);
        var normalizedAlias = NormalizeAlias(alias);
        lock (gate)
        {
            return Resolve(ReadIndex(normalizedAppId), normalizedAppId, normalizedAlias, secretResolver)?.Metadata;
        }
    }

    private ResolvedSecret? Resolve(
        IReadOnlyList<SimulatorAgentSecretMetadata> index,
        string appId,
        string alias,
        Func<string, string?>? secretResolver)
    {
        var storedMetadata = index.FirstOrDefault(item => string.Equals(
            item.Alias,
            alias,
            StringComparison.OrdinalIgnoreCase));
        var storedValue = storedMetadata is null
            ? null
            : encryptedStorage.Get(BuildValueKey(appId, storedMetadata.Alias));
        if (storedMetadata is not null && storedValue is not null)
        {
            return new ResolvedSecret(storedMetadata, storedValue);
        }

        var environmentValue = (secretResolver ?? environmentVariableResolver)(alias);
        if (string.IsNullOrEmpty(environmentValue))
        {
            return null;
        }
        if (environmentValue.Length > MaximumSecretValueCharacters)
        {
            throw new InvalidOperationException(
                $"Environment variable for test secret '{alias}' must be {MaximumSecretValueCharacters:N0} characters or fewer.");
        }
        return new ResolvedSecret(
            new SimulatorAgentSecretMetadata(
                alias,
                appId,
                EnvironmentVersionId,
                DateTimeOffset.MinValue),
            environmentValue);
    }

    private IReadOnlyList<SimulatorAgentSecretMetadata> ReadIndex(string appId)
    {
        var source = encryptedStorage.Get(BuildIndexKey(appId));
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<SimulatorAgentSecretMetadata>>(source, jsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void WriteIndex(string appId, IReadOnlyList<SimulatorAgentSecretMetadata> metadata)
        => encryptedStorage.Set(BuildIndexKey(appId), JsonSerializer.Serialize(metadata, jsonOptions));

    private static IReadOnlyList<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null)
        {
            return [];
        }
        return aliases
            .Select(NormalizeAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeAppId(string? appId)
        => string.IsNullOrWhiteSpace(appId)
            ? throw new ArgumentException("An app ID is required for test secrets.", nameof(appId))
            : appId.Trim();

    private static string NormalizeAlias(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var normalized = alias.Trim();
        if (!SecretAliasPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Secret aliases must start with a letter or number and contain only letters, numbers, '.', '_' or '-' (maximum 128 characters).",
                nameof(alias));
        }
        return normalized;
    }

    private static string BuildIndexKey(string appId)
        => $"{StoragePrefix}.{Hash(appId.ToLowerInvariant())}.index";

    private static string BuildValueKey(string appId, string alias)
        => $"{StoragePrefix}.{Hash(appId.ToLowerInvariant())}.{Hash(alias.ToLowerInvariant())}";

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretAliasPattern();
}
