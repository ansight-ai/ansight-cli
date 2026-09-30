namespace Ansight.Host.Pairing;

using System.Text;

[Export(typeof(IPairingConfigCache))]
internal sealed class PairingConfigCache : IPairingConfigCache
{
    private const string StorageKey = "device-registration-cache-v2";
    private static readonly TimeSpan LocalRegistrationLifetime = TimeSpan.FromDays(90);

    private readonly object gate = new();
    private readonly Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage;
    private PairingCacheDocument document;

    [ImportingConstructor]
    public PairingConfigCache(
        IApplicationPaths applicationPaths,
        Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage)
        : this(encryptedStorage)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
    }

    public PairingConfigCache(Ansight.Infrastructure.Security.IEncryptedStorage encryptedStorage)
    {
        this.encryptedStorage = encryptedStorage ?? throw new ArgumentNullException(nameof(encryptedStorage));
        document = LoadDocument();
    }

    public void Add(PairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Add(
            new CachedPairingConfig
            {
                Config = config,
                Consumed = false
            });
    }

    public void Add(CachedPairingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (gate)
        {
            ReloadDocument();
            document.Items.RemoveAll(item =>
                string.Equals(item.Config.ConfigId, config.Config.ConfigId, StringComparison.Ordinal));
            document.RemovedConfigIds.RemoveAll(configId =>
                string.Equals(configId, config.Config.ConfigId, StringComparison.Ordinal));
            document.Items.Add(Clone(config));
            Persist();
        }
    }

    public IReadOnlyList<CachedPairingConfig> GetSnapshot()
    {
        lock (gate)
        {
            ReloadDocument();
            return document.Items.Select(Clone).ToArray();
        }
    }

    public CachedPairingConfig? Find(string configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
        {
            return null;
        }

        lock (gate)
        {
            ReloadDocument();
            var item = document.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Config.ConfigId, configId.Trim(), StringComparison.Ordinal));
            return item is null ? null : Clone(item);
        }
    }

    public bool Remove(string configId)
    {
        if (string.IsNullOrWhiteSpace(configId))
        {
            return false;
        }

        var normalizedConfigId = configId.Trim();
        lock (gate)
        {
            ReloadDocument();
            var removed = document.Items.RemoveAll(item =>
                string.Equals(item.Config.ConfigId, normalizedConfigId, StringComparison.Ordinal));
            if (removed == 0)
            {
                return false;
            }

            AddRemovedConfigId(document, normalizedConfigId);
            document.ClientGrants.RemoveAll(grant =>
                string.Equals(grant.ConfigId, normalizedConfigId, StringComparison.Ordinal));
            Persist();
            return true;
        }
    }

    public DeviceConnectionAuthorization AuthorizeConnection(
        DeviceConnectionAttempt attempt,
        RuntimeIdentity hostIdentity)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(hostIdentity);

        if (string.IsNullOrWhiteSpace(attempt.InviteId)
            || string.IsNullOrWhiteSpace(attempt.AppId)
            || PairingConfig.TargetsAnyApp(attempt.AppId)
            || string.IsNullOrWhiteSpace(attempt.DeviceId)
            || string.IsNullOrWhiteSpace(attempt.AccessToken))
        {
            return DeviceConnectionAuthorization.Reject(
                "EnrollmentRequired",
                "Scan a current Ansight QR code to register this device.");
        }

        lock (gate)
        {
            ReloadDocument();
            var now = DateTimeOffset.UtcNow;
            var item = document.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.Config.ConfigId, attempt.InviteId, StringComparison.Ordinal));
            if (item?.Config.Enrollment is not { } enrollment
                || !PairingProtocolPolicy.IsEnabled(item.Config)
                || (!PairingConfig.TargetsAnyApp(item.Config.AppId)
                    && !string.Equals(item.Config.AppId, attempt.AppId, StringComparison.Ordinal)))
            {
                return DeviceConnectionAuthorization.Reject(
                    "EnrollmentRequired",
                    "Ansight does not recognize this enrollment invite. Scan a fresh QR code.");
            }

            var existingRegistration = document.ClientGrants.FirstOrDefault(grant =>
                string.Equals(grant.ConfigId, item.Config.ConfigId, StringComparison.Ordinal)
                && string.Equals(grant.AppId, attempt.AppId, StringComparison.Ordinal)
                && string.Equals(grant.DeviceId, attempt.DeviceId, StringComparison.Ordinal));
            if (existingRegistration is not null)
            {
                if (!existingRegistration.IsActive(now))
                {
                    return DeviceConnectionAuthorization.Reject(
                        "RegistrationExpired",
                        "This device registration has expired or been revoked. Scan a fresh QR code.");
                }

                if (!SecretMatches(existingRegistration.SecretHash, attempt.AccessToken))
                {
                    return DeviceConnectionAuthorization.Reject(
                        "AccessTokenInvalid",
                        "The stored Ansight access token is invalid. Scan a fresh QR code.");
                }

                return DeviceConnectionAuthorization.Accept(
                    Clone(existingRegistration),
                    isNewRegistration: false);
            }

            if (item.Config.ExpiresAt < now || enrollment.ExpiresAt < now)
            {
                return DeviceConnectionAuthorization.Reject(
                    "EnrollmentExpired",
                    "This enrollment invite has expired. Scan a fresh QR code.");
            }

            if (item.EnrollmentUseCount >= enrollment.MaxUses
                || string.IsNullOrWhiteSpace(enrollment.Secret))
            {
                return DeviceConnectionAuthorization.Reject(
                    "EnrollmentConsumed",
                    "This enrollment invite has already registered a device. Scan a fresh QR code.");
            }

            if (!FixedTimeEqualsUtf8(enrollment.Secret, attempt.AccessToken))
            {
                return DeviceConnectionAuthorization.Reject(
                    "AccessTokenInvalid",
                    "The enrollment access token is invalid. Scan a fresh QR code.");
            }

            var registration = new PairingClientGrant
            {
                GrantId = CryptoUtil.CreateBase64UrlRandom(16),
                SecretHash = ComputeSecretHash(attempt.AccessToken),
                HostId = hostIdentity.HostId,
                ConfigId = item.Config.ConfigId,
                AppId = attempt.AppId.Trim(),
                DeviceId = attempt.DeviceId.Trim(),
                DeviceName = NormalizeDeviceName(attempt.DeviceName),
                MaxToolPolicy = PairingToolPolicy.Normalize(enrollment.MaxToolPolicy),
                IssuedAt = now,
                ExpiresAt = enrollment.GrantExpiresAt
            };

            item.EnrollmentUseCount++;
            item.Consumed = true;
            item.Config.Enrollment.Secret = string.Empty;
            document.ClientGrants.Add(Clone(registration));
            Persist();
            return DeviceConnectionAuthorization.Accept(
                registration,
                isNewRegistration: true);
        }
    }

    public DeviceConnectionAuthorization AuthorizeLocalConnection(
        DeviceConnectionAttempt attempt,
        RuntimeIdentity hostIdentity)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(hostIdentity);

        if (string.IsNullOrWhiteSpace(attempt.AppId)
            || PairingConfig.TargetsAnyApp(attempt.AppId)
            || string.IsNullOrWhiteSpace(attempt.DeviceId)
            || string.IsNullOrWhiteSpace(attempt.AccessToken))
        {
            return DeviceConnectionAuthorization.Reject(
                "LocalEnrollmentInvalid",
                "The local developer registration request is incomplete.");
        }

        lock (gate)
        {
            ReloadDocument();
            var now = DateTimeOffset.UtcNow;
            var appId = attempt.AppId.Trim();
            var deviceId = attempt.DeviceId.Trim();
            var existingRegistration = document.ClientGrants.FirstOrDefault(grant =>
                string.Equals(grant.AppId, appId, StringComparison.Ordinal)
                && string.Equals(grant.DeviceId, deviceId, StringComparison.Ordinal)
                && grant.ConfigId.StartsWith("local:", StringComparison.Ordinal));
            if (existingRegistration is not null)
            {
                if (existingRegistration.RevokedAt is not null)
                {
                    return DeviceConnectionAuthorization.Reject(
                        "RegistrationRevoked",
                        "This local developer registration has been revoked in Ansight.");
                }

                if (!SecretMatches(existingRegistration.SecretHash, attempt.AccessToken))
                {
                    return DeviceConnectionAuthorization.Reject(
                        "AccessTokenInvalid",
                        "The local developer registration token is invalid.");
                }

                if (existingRegistration.ExpiresAt >= now)
                {
                    if (ApplyLocalDeveloperPolicy(existingRegistration))
                    {
                        Persist();
                    }

                    return DeviceConnectionAuthorization.Accept(
                        Clone(existingRegistration),
                        isNewRegistration: false);
                }

                document.ClientGrants.Remove(existingRegistration);
            }

            var registration = new PairingClientGrant
            {
                GrantId = CryptoUtil.CreateBase64UrlRandom(16),
                SecretHash = ComputeSecretHash(attempt.AccessToken),
                HostId = hostIdentity.HostId,
                ConfigId = $"local:{CryptoUtil.CreateBase64UrlRandom(16)}",
                AppId = appId,
                DeviceId = deviceId,
                DeviceName = NormalizeDeviceName(attempt.DeviceName),
                MaxToolPolicy = GetLocalDeveloperPolicy(),
                IssuedAt = now,
                ExpiresAt = now.Add(LocalRegistrationLifetime)
            };

            document.ClientGrants.Add(Clone(registration));
            Persist();
            return DeviceConnectionAuthorization.Accept(
                registration,
                isNewRegistration: true);
        }
    }

    public bool HasActiveGrant(string configId, string appId)
    {
        if (string.IsNullOrWhiteSpace(configId) || string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        lock (gate)
        {
            ReloadDocument();
            var now = DateTimeOffset.UtcNow;
            return document.ClientGrants.Any(grant =>
                string.Equals(grant.ConfigId, configId, StringComparison.Ordinal)
                && string.Equals(grant.AppId, appId, StringComparison.Ordinal)
                && grant.IsActive(now));
        }
    }

    public bool RevokeGrant(string grantId, string reason)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return false;
        }

        lock (gate)
        {
            ReloadDocument();
            var grant = document.ClientGrants.FirstOrDefault(candidate =>
                string.Equals(candidate.GrantId, grantId.Trim(), StringComparison.Ordinal));
            if (grant is null || grant.RevokedAt is not null)
            {
                return false;
            }

            grant.RevokedAt = DateTimeOffset.UtcNow;
            grant.RevocationReason = string.IsNullOrWhiteSpace(reason)
                ? "Revoked by Ansight."
                : reason.Trim();
            Persist();
            return true;
        }
    }

    private void ReloadDocument()
    {
        if (TryLoadDocument(out var loadedDocument) || document.Items.Count == 0)
        {
            document = loadedDocument;
        }
    }

    private PairingCacheDocument LoadDocument()
        => TryLoadDocument(out var loadedDocument)
            ? loadedDocument
            : new PairingCacheDocument();

    private bool TryLoadDocument(out PairingCacheDocument loadedDocument)
    {
        var json = encryptedStorage.Get(StorageKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            loadedDocument = new PairingCacheDocument();
            return false;
        }

        try
        {
            loadedDocument =
                JsonSerializer.Deserialize<PairingCacheDocument>(json, JsonUtil.Compact)
                ?? new PairingCacheDocument();
            Normalize(loadedDocument);
            return true;
        }
        catch
        {
            loadedDocument = new PairingCacheDocument();
            return false;
        }
    }

    private void Persist()
    {
        encryptedStorage.Set(StorageKey, JsonSerializer.Serialize(document, JsonUtil.Pretty));
    }

    private static void Normalize(PairingCacheDocument target)
    {
        target.Items ??= [];
        target.RemovedConfigIds ??= [];
        target.ClientGrants ??= [];

        target.Items.RemoveAll(item => !IsValidCachedConfig(item));
        target.ClientGrants.RemoveAll(grant =>
            grant is null
            || string.IsNullOrWhiteSpace(grant.GrantId)
            || string.IsNullOrWhiteSpace(grant.SecretHash)
            || string.IsNullOrWhiteSpace(grant.DeviceId));
        target.RemovedConfigIds = target.RemovedConfigIds
            .Where(configId => !string.IsNullOrWhiteSpace(configId))
            .Select(configId => configId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        target.Items.RemoveAll(item =>
            target.RemovedConfigIds.Contains(item.Config.ConfigId, StringComparer.Ordinal));
    }

    private static bool IsValidCachedConfig(CachedPairingConfig? item)
    {
        return item?.Config is { } config
               && string.Equals(config.Schema, PairingConfig.SchemaName, StringComparison.Ordinal)
               && !string.IsNullOrWhiteSpace(config.ConfigId)
               && config.Host is not null
               && config.Enrollment is not null;
    }

    private static void AddRemovedConfigId(PairingCacheDocument target, string configId)
    {
        if (!target.RemovedConfigIds.Contains(configId, StringComparer.Ordinal))
        {
            target.RemovedConfigIds.Add(configId);
        }
    }

    private static bool ApplyLocalDeveloperPolicy(PairingClientGrant registration)
    {
        var maxToolPolicy = GetLocalDeveloperPolicy();
        if (string.Equals(registration.MaxToolPolicy, maxToolPolicy, StringComparison.Ordinal))
        {
            return false;
        }

        registration.MaxToolPolicy = maxToolPolicy;
        return true;
    }

    private static string GetLocalDeveloperPolicy()
        => "write";

    private static string NormalizeDeviceName(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "Device" : value.Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static string ComputeSecretHash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool SecretMatches(string encodedHash, string suppliedSecret)
    {
        try
        {
            var expected = Convert.FromBase64String(encodedHash);
            var supplied = Convert.FromBase64String(ComputeSecretHash(suppliedSecret));
            try
            {
                return CryptographicOperations.FixedTimeEquals(expected, supplied);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                CryptographicOperations.ZeroMemory(supplied);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool FixedTimeEqualsUtf8(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        try
        {
            return expectedBytes.Length == actualBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static CachedPairingConfig Clone(CachedPairingConfig source)
    {
        return new CachedPairingConfig
        {
            Config = Clone(source.Config),
            Consumed = source.Consumed,
            EnrollmentUseCount = source.EnrollmentUseCount,
            SourceKind = string.IsNullOrWhiteSpace(source.SourceKind) ? null : source.SourceKind.Trim(),
            SourceTeamId = string.IsNullOrWhiteSpace(source.SourceTeamId) ? null : source.SourceTeamId.Trim(),
            SourceTeamName = string.IsNullOrWhiteSpace(source.SourceTeamName) ? null : source.SourceTeamName.Trim(),
            CompactCode = string.IsNullOrWhiteSpace(source.CompactCode) ? null : source.CompactCode.Trim()
        };
    }

    private static PairingConfig Clone(PairingConfig source)
    {
        return new PairingConfig
        {
            Schema = source.Schema,
            ConfigId = source.ConfigId,
            AppId = source.AppId,
            AppName = source.AppName,
            IssuedAt = source.IssuedAt,
            ExpiresAt = source.ExpiresAt,
            MinProtocolVersion = source.MinProtocolVersion,
            AllowedTransports = source.AllowedTransports is null ? [] : [.. source.AllowedTransports],
            Host = new PairingHost
            {
                HostId = source.Host.HostId,
                HostName = source.Host.HostName,
                DiscoveryPort = source.Host.DiscoveryPort
            },
            Enrollment = source.Enrollment is null
                ? null
                : new PairingEnrollment
                {
                    Secret = source.Enrollment.Secret,
                    ExpiresAt = source.Enrollment.ExpiresAt,
                    GrantExpiresAt = source.Enrollment.GrantExpiresAt,
                    MaxUses = source.Enrollment.MaxUses,
                    MaxToolPolicy = PairingToolPolicy.Normalize(source.Enrollment.MaxToolPolicy)
                }
        };
    }

    private static PairingClientGrant Clone(PairingClientGrant source)
    {
        return new PairingClientGrant
        {
            GrantId = source.GrantId,
            SecretHash = source.SecretHash,
            HostId = source.HostId,
            ConfigId = source.ConfigId,
            AppId = source.AppId,
            DeviceId = source.DeviceId,
            DeviceName = source.DeviceName,
            MaxToolPolicy = PairingToolPolicy.Normalize(source.MaxToolPolicy),
            IssuedAt = source.IssuedAt,
            ExpiresAt = source.ExpiresAt,
            RevokedAt = source.RevokedAt,
            RevocationReason = source.RevocationReason
        };
    }
}
