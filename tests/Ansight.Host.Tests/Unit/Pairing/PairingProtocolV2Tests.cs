using System.Security.Cryptography;

namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class EnrollmentProtocolTests
{
    [Fact]
    public void Enrollment_RegistersOneInstallationAndSupportsReconnectUntilRevoked()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        var config = CreateConfig(hostIdentity);
        var accessToken = config.Enrollment!.Secret;
        cache.Add(config);

        var attempt = new DeviceConnectionAttempt(
            config.ConfigId,
            config.AppId,
            "device-1",
            "Example Phone",
            accessToken);
        var enrollment = cache.AuthorizeConnection(attempt, hostIdentity);

        Assert.True(enrollment.Accepted, enrollment.ReasonMessage);
        var registration = Assert.IsType<PairingClientGrant>(enrollment.Grant);
        Assert.Equal("device-1", registration.DeviceId);
        Assert.Equal("read", registration.MaxToolPolicy);

        var consumedInvite = Assert.IsType<CachedPairingConfig>(cache.Find(config.ConfigId));
        Assert.Equal(1, consumedInvite.EnrollmentUseCount);
        Assert.True(consumedInvite.Consumed);
        Assert.Equal(string.Empty, consumedInvite.Config.Enrollment!.Secret);

        var reconnect = cache.AuthorizeConnection(attempt, hostIdentity);
        Assert.True(reconnect.Accepted, reconnect.ReasonMessage);
        Assert.Equal(registration.GrantId, reconnect.Grant?.GrantId);

        var otherInstallation = cache.AuthorizeConnection(
            attempt with { DeviceId = "device-2" },
            hostIdentity);
        Assert.False(otherInstallation.Accepted);
        Assert.Equal("EnrollmentConsumed", otherInstallation.ReasonCode);

        Assert.True(cache.RevokeGrant(registration.GrantId, "unit test"));
        var revokedReconnect = cache.AuthorizeConnection(attempt, hostIdentity);
        Assert.False(revokedReconnect.Accepted);
        Assert.Equal("RegistrationExpired", revokedReconnect.ReasonCode);
    }

    [Fact]
    public void Enrollment_RejectsInvalidTokenWithoutConsumingInvite()
    {
        using var hostIdentity = CreateHostIdentity();
        var cache = new PairingConfigCache(new InMemoryEncryptedStorage());
        var config = CreateConfig(hostIdentity);
        cache.Add(config);

        var result = cache.AuthorizeConnection(
            new DeviceConnectionAttempt(
                config.ConfigId,
                config.AppId,
                "device-1",
                "Example Phone",
                CryptoUtil.CreateBase64UrlRandom(32)),
            hostIdentity);

        Assert.False(result.Accepted);
        Assert.Equal("AccessTokenInvalid", result.ReasonCode);
        Assert.Equal(0, cache.Find(config.ConfigId)?.EnrollmentUseCount);
    }

    [Fact]
    public void ProtocolPolicy_AcceptsOnlyCurrentEnrollmentInviteSchema()
    {
        Assert.True(PairingProtocolPolicy.IsEnabledSchema(PairingConfig.SchemaName));
        Assert.False(PairingProtocolPolicy.IsEnabledSchema("unsupported"));
        Assert.False(PairingProtocolPolicy.IsEnabledSchema(null));
    }

    private static PairingConfig CreateConfig(RuntimeIdentity hostIdentity)
    {
        var now = DateTimeOffset.UtcNow;
        return new PairingConfig
        {
            Schema = PairingConfig.SchemaName,
            ConfigId = Guid.NewGuid().ToString("N"),
            AppId = "com.example.enrollment",
            AppName = "Enrollment Test",
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(30),
            MinProtocolVersion = 2,
            AllowedTransports = [PairingTransportNames.Ws],
            Host = new PairingHost
            {
                HostId = hostIdentity.HostId,
                HostName = hostIdentity.HostName,
                DiscoveryPort = ProtocolDefaults.DiscoveryPort
            },
            Enrollment = new PairingEnrollment
            {
                Secret = CryptoUtil.CreateBase64UrlRandom(32),
                ExpiresAt = now.AddMinutes(30),
                GrantExpiresAt = now.AddDays(30),
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
}
