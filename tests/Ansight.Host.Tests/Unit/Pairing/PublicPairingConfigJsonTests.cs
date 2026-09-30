namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class PublicPairingConfigJsonTests
{
    [Fact]
    public void SerializeAndDeserialize_CurrentInvite_RoundTripsContract()
    {
        var source = CreateInvite();

        var json = PublicPairingConfigJson.Serialize(source, indented: true);
        var result = PublicPairingConfigJson.TryDeserialize(json);

        Assert.NotNull(result);
        Assert.Equal(PairingConfig.SchemaName, result.Schema);
        Assert.Equal(source.ConfigId, result.ConfigId);
        Assert.Equal(source.Enrollment?.Secret, result.Enrollment?.Secret);
        Assert.Equal([PairingTransportNames.Ws], result.AllowedTransports);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(source.ConfigId, root.GetProperty("inviteId").GetString());
        Assert.Equal(source.Enrollment?.Secret, root.GetProperty("enrollment").GetProperty("accessToken").GetString());
        Assert.False(root.TryGetProperty("configId", out _));
        Assert.False(root.TryGetProperty("signature", out _));
        Assert.False(root.TryGetProperty("challenge", out _));
    }

    [Fact]
    public void TryDeserialize_EnrollmentInviteDocument_ReturnsNestedInvite()
    {
        var source = CreateInvite();
        var document = PairingConfigDocumentFactory.Create(
            source,
            ["192.0.2.10"],
            "Host",
            "Development",
            "unit-test");
        var json = PairingConfigDocumentFactory.Serialize(document);

        var result = PublicPairingConfigJson.TryDeserialize(json);

        Assert.NotNull(result);
        Assert.Equal(source.ConfigId, result.ConfigId);
        Assert.Equal(source.AppId, result.AppId);
    }

    [Fact]
    public void Serialize_UnsupportedSchema_Throws()
    {
        var invite = CreateInvite();
        invite.Schema = "unsupported";

        Assert.Throws<NotSupportedException>(
            () => PublicPairingConfigJson.Serialize(invite));
        Assert.Null(PublicPairingConfigJson.TryDeserialize(JsonSerializer.Serialize(invite)));
    }

    [Fact]
    public void PairingProtocolPolicy_EnablesOnlyCurrentSchema()
    {
        Assert.Equal(PairingConfig.SchemaName, PairingProtocolPolicy.DefaultSchema);
        Assert.True(PairingProtocolPolicy.IsEnabledSchema(PairingConfig.SchemaName));
        Assert.False(PairingProtocolPolicy.IsEnabledSchema("unsupported"));
        Assert.False(PairingProtocolPolicy.IsEnabledSchema(null));
    }

    private static PairingConfig CreateInvite()
    {
        var now = DateTimeOffset.UtcNow;
        return new PairingConfig
        {
            Schema = PairingConfig.SchemaName,
            ConfigId = "invite-current",
            AppId = "ai.ansight.current",
            AppName = "Current App",
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(10),
            MinProtocolVersion = 2,
            AllowedTransports = [PairingTransportNames.Ws],
            Host = new PairingHost
            {
                HostId = "host-id",
                HostName = "Host",
                DiscoveryPort = ProtocolDefaults.DiscoveryPort
            },
            Enrollment = new PairingEnrollment
            {
                Secret = CryptoUtil.CreateBase64UrlRandom(32),
                ExpiresAt = now.AddMinutes(10),
                GrantExpiresAt = now.AddDays(14),
                MaxUses = 1,
                MaxToolPolicy = "read"
            }
        };
    }
}
