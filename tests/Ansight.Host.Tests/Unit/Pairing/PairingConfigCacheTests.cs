using System.Security.Cryptography;

namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class PairingConfigCacheTests
{
    [Fact]
    public void GetSnapshot_ReturnsClonedInvites()
    {
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));

        var firstSnapshot = Assert.Single(cache.GetSnapshot());
        firstSnapshot.Config.AppName = "Changed";

        var secondSnapshot = Assert.Single(cache.GetSnapshot());
        Assert.Equal("Example App", secondSnapshot.Config.AppName);
    }

    [Fact]
    public void AuthorizeConnection_RegistersAndReconnectsSameInstallation()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));
        var attempt = CreateAttempt("invite-1", "token-1", "app-1", "device-1");

        var first = cache.AuthorizeConnection(attempt, hostIdentity);
        var second = cache.AuthorizeConnection(attempt, hostIdentity);
        var stored = Assert.Single(cache.GetSnapshot());

        Assert.True(first.Accepted, first.ReasonMessage);
        Assert.True(second.Accepted, second.ReasonMessage);
        Assert.True(first.IsNewRegistration);
        Assert.False(second.IsNewRegistration);
        Assert.Equal(first.Grant?.GrantId, second.Grant?.GrantId);
        Assert.True(stored.Consumed);
        Assert.Equal(1, stored.EnrollmentUseCount);
        Assert.Equal(string.Empty, stored.Config.Enrollment?.Secret);
    }

    [Fact]
    public void AuthorizeConnection_AnyAppInviteBindsGrantToScanningInstallation()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(CreateConfig(
            "invite-any-app",
            "token-any-app",
            PairingConfig.AnyAppId));
        var attempt = CreateAttempt(
            "invite-any-app",
            "token-any-app",
            "com.example.scanning-app",
            "device-1");

        var first = cache.AuthorizeConnection(attempt, hostIdentity);
        var reconnect = cache.AuthorizeConnection(attempt, hostIdentity);
        var otherApp = cache.AuthorizeConnection(
            CreateAttempt(
                "invite-any-app",
                "token-any-app",
                "com.example.other-app",
                "device-2"),
            hostIdentity);

        Assert.True(first.Accepted, first.ReasonMessage);
        Assert.True(first.IsNewRegistration);
        Assert.Equal("com.example.scanning-app", first.Grant?.AppId);
        Assert.True(reconnect.Accepted, reconnect.ReasonMessage);
        Assert.False(reconnect.IsNewRegistration);
        Assert.Equal(first.Grant?.GrantId, reconnect.Grant?.GrantId);
        Assert.True(cache.HasActiveGrant("invite-any-app", "com.example.scanning-app"));
        Assert.False(otherApp.Accepted);
        Assert.Equal("EnrollmentConsumed", otherApp.ReasonCode);
    }

    [Theory]
    [InlineData("", "token-1", "app-1", "device-1", "EnrollmentRequired")]
    [InlineData("missing", "token-1", "app-1", "device-1", "EnrollmentRequired")]
    [InlineData("invite-1", "token-1", "other-app", "device-1", "EnrollmentRequired")]
    [InlineData("invite-1", "token-1", PairingConfig.AnyAppId, "device-1", "EnrollmentRequired")]
    [InlineData("invite-1", "wrong-token", "app-1", "device-1", "AccessTokenInvalid")]
    public void AuthorizeConnection_RejectsInvalidInputs(
        string inviteId,
        string token,
        string appId,
        string deviceId,
        string expectedReasonCode)
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));

        var result = cache.AuthorizeConnection(
            CreateAttempt(inviteId, token, appId, deviceId),
            hostIdentity);

        Assert.False(result.Accepted);
        Assert.Equal(expectedReasonCode, result.ReasonCode);
    }

    [Fact]
    public void AuthorizeConnection_RejectsExpiredInvite()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(
            CreateConfig(
                "invite-1",
                "token-1",
                "app-1",
                DateTimeOffset.UtcNow.AddMinutes(-1)));

        var result = cache.AuthorizeConnection(
            CreateAttempt("invite-1", "token-1", "app-1", "device-1"),
            hostIdentity);

        Assert.False(result.Accepted);
        Assert.Equal("EnrollmentExpired", result.ReasonCode);
    }

    [Fact]
    public void AuthorizeLocalConnection_RegistersAndReconnectsWithoutInvite()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        var attempt = CreateAttempt(string.Empty, "local-token", "app-1", "device-1");

        var first = cache.AuthorizeLocalConnection(attempt, hostIdentity);
        var second = cache.AuthorizeLocalConnection(attempt, hostIdentity);

        Assert.True(first.Accepted, first.ReasonMessage);
        Assert.True(second.Accepted, second.ReasonMessage);
        Assert.True(first.IsNewRegistration);
        Assert.False(second.IsNewRegistration);
        var firstGrant = Assert.IsType<PairingClientGrant>(first.Grant);
        var secondGrant = Assert.IsType<PairingClientGrant>(second.Grant);
        Assert.Equal(firstGrant.GrantId, secondGrant.GrantId);
        Assert.StartsWith("local:", firstGrant.ConfigId, StringComparison.Ordinal);
        Assert.Equal("write", firstGrant.MaxToolPolicy);
        Assert.Empty(cache.GetSnapshot());
    }

    [Fact]
    public void AuthorizeLocalConnection_UpgradesExistingReadOnlyGrant()
    {
        const string storageKey = "device-registration-cache-v2";
        using var hostIdentity = CreateHostIdentity();
        var storage = new InMemoryEncryptedStorage();
        var cache = new PairingConfigCache(storage);
        var attempt = CreateAttempt(string.Empty, "local-token", "app-1", "device-1");
        var first = cache.AuthorizeLocalConnection(attempt, hostIdentity);
        Assert.True(first.Accepted, first.ReasonMessage);

        var storedDocument = Assert.IsType<PairingCacheDocument>(
            JsonSerializer.Deserialize<PairingCacheDocument>(
                Assert.IsType<string>(storage.Get(storageKey)),
                JsonUtil.Compact));
        var storedGrant = Assert.Single(storedDocument.ClientGrants);
        storedGrant.MaxToolPolicy = "read";
        storage.Set(storageKey, JsonSerializer.Serialize(storedDocument, JsonUtil.Pretty));

        var reconnect = cache.AuthorizeLocalConnection(attempt, hostIdentity);

        Assert.True(reconnect.Accepted, reconnect.ReasonMessage);
        var reconnectGrant = Assert.IsType<PairingClientGrant>(reconnect.Grant);
        Assert.Equal(first.Grant?.GrantId, reconnectGrant.GrantId);
        Assert.Equal("write", reconnectGrant.MaxToolPolicy);

        var migratedDocument = Assert.IsType<PairingCacheDocument>(
            JsonSerializer.Deserialize<PairingCacheDocument>(
                Assert.IsType<string>(storage.Get(storageKey)),
                JsonUtil.Compact));
        var migratedGrant = Assert.Single(migratedDocument.ClientGrants);
        Assert.Equal("write", migratedGrant.MaxToolPolicy);
    }

    [Fact]
    public void AuthorizeLocalConnection_RejectsChangedInstallationToken()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        var first = cache.AuthorizeLocalConnection(
            CreateAttempt(string.Empty, "local-token", "app-1", "device-1"),
            hostIdentity);

        var changedToken = cache.AuthorizeLocalConnection(
            CreateAttempt(string.Empty, "changed-token", "app-1", "device-1"),
            hostIdentity);

        Assert.True(first.Accepted, first.ReasonMessage);
        Assert.False(changedToken.Accepted);
        Assert.Equal("AccessTokenInvalid", changedToken.ReasonCode);
    }

    [Fact]
    public void Add_CachedInvitePreservesMetadata()
    {
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        cache.Add(new CachedPairingConfig
        {
            Config = CreateConfig("invite-team", "token-team", "app-team"),
            Consumed = true,
            EnrollmentUseCount = 1,
            SourceKind = "team-sync",
            SourceTeamId = "team-1",
            SourceTeamName = "Team One",
            CompactCode = "ans2:compact"
        });

        var snapshot = Assert.Single(cache.GetSnapshot());
        Assert.True(snapshot.Consumed);
        Assert.Equal(1, snapshot.EnrollmentUseCount);
        Assert.Equal("team-sync", snapshot.SourceKind);
        Assert.Equal("team-1", snapshot.SourceTeamId);
        Assert.Equal("Team One", snapshot.SourceTeamName);
        Assert.Equal("ans2:compact", snapshot.CompactCode);
    }

    [Fact]
    public void GetSnapshot_WhenBackingStoreReadIsUnavailable_RetainsIssuedInviteInMemory()
    {
        var storage = new ReadBlockingEncryptedStorage();
        var cache = new PairingConfigCache(storage);
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));

        storage.BlockReads = true;

        Assert.Equal("invite-1", Assert.Single(cache.GetSnapshot()).Config.ConfigId);
    }

    [Fact]
    public void Remove_DeletesInviteAndItsRegistration()
    {
        using var hostIdentity = CreateHostIdentity();
        var storage = new InMemoryEncryptedStorage();
        var cache = new PairingConfigCache(storage);
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));
        var enrollment = cache.AuthorizeConnection(
            CreateAttempt("invite-1", "token-1", "app-1", "device-1"),
            hostIdentity);
        Assert.True(enrollment.Accepted);

        Assert.True(cache.Remove("invite-1"));

        Assert.Empty(cache.GetSnapshot());
        Assert.False(cache.HasActiveGrant("invite-1", "app-1"));
        Assert.Null(new PairingConfigCache(storage).Find("invite-1"));
    }

    [Fact]
    public void Add_ClearsPreviousRemovalMarker()
    {
        var storage = new InMemoryEncryptedStorage();
        var cache = new PairingConfigCache(storage);
        cache.Add(CreateConfig("invite-1", "token-1", "app-1"));
        Assert.True(cache.Remove("invite-1"));

        cache.Add(CreateConfig("invite-1", "token-2", "app-1"));

        Assert.Equal("token-2", Assert.Single(cache.GetSnapshot()).Config.Enrollment?.Secret);
    }

    private static DeviceConnectionAttempt CreateAttempt(
        string inviteId,
        string token,
        string appId,
        string deviceId)
        => new(inviteId, appId, deviceId, "Example Phone", token);

    private static PairingConfig CreateConfig(
        string inviteId,
        string token,
        string appId,
        DateTimeOffset? expiresAt = null)
    {
        var expiry = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
        return new PairingConfig
        {
            Schema = PairingConfig.SchemaName,
            ConfigId = inviteId,
            AppId = appId,
            AppName = "Example App",
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = expiry,
            MinProtocolVersion = 2,
            AllowedTransports = [PairingTransportNames.Ws],
            Host = new PairingHost
            {
                HostId = "host-1",
                HostName = "Host",
                DiscoveryPort = 45123
            },
            Enrollment = new PairingEnrollment
            {
                Secret = token,
                ExpiresAt = expiry,
                GrantExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                MaxUses = 1,
                MaxToolPolicy = "read"
            }
        };
    }

    private static RuntimeIdentity CreateHostIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return RuntimeIdentity.FromPrivateKey("Host", key.ExportPkcs8PrivateKey());
    }

    private sealed class ReadBlockingEncryptedStorage : IEncryptedStorage
    {
        private readonly InMemoryEncryptedStorage inner = new();

        public bool BlockReads { get; set; }

        public string? Get(string key)
            => BlockReads ? null : inner.Get(key);

        public void Set(string key, string? value)
            => inner.Set(key, value);

        public void Remove(string key)
            => inner.Remove(key);

        public void Clear()
            => inner.Clear();
    }
}
