using System.IO.Compression;
using System.Text;

namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class PairingConfigExchangeTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsEnrollmentExport()
    {
        var cachedInvite = CreateCachedInvite("invite-1", "app-1");

        var json = PairingConfigExchange.Serialize(cachedInvite);
        var result = PairingConfigExchange.TryDeserialize(json);

        Assert.True(result.IsSuccess, result.Message);
        var imported = Assert.IsType<CachedPairingConfig>(result.Config);
        Assert.Equal(cachedInvite.Config.ConfigId, imported.Config.ConfigId);
        Assert.Equal(cachedInvite.Config.AppId, imported.Config.AppId);
        Assert.Equal(cachedInvite.Config.Host.HostId, imported.Config.Host.HostId);
        Assert.Equal(cachedInvite.Config.Enrollment?.Secret, imported.Config.Enrollment?.Secret);
        Assert.Equal(cachedInvite.Consumed, imported.Consumed);
    }

    [Fact]
    public void TryDeserialize_RejectsPublicInviteAsPrivateExport()
    {
        var publicJson = PublicPairingConfigJson.Serialize(
            CreateCachedInvite("invite-1", "app-1").Config,
            indented: true);

        var result = PairingConfigExchange.TryDeserialize(publicJson);

        Assert.False(result.IsSuccess);
        Assert.Contains("enrollment export", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDeserializeImportPayload_FullExportPreservesInvite()
    {
        var cachedInvite = CreateCachedInvite("invite-1", "app-1");

        var result = PairingConfigExchange.TryDeserializeImportPayload(
            PairingConfigExchange.Serialize(cachedInvite));

        Assert.True(result.IsSuccess, result.Message);
        var payload = Assert.IsType<PairingConfigImportPayload>(result.Payload);
        Assert.Equal(PairingConfigImportKind.FullConfigExport, payload.Kind);
        Assert.Equal("app-1", payload.AppId);
        Assert.Equal("Example App", payload.AppName);
        Assert.Equal("invite-1", payload.SourceConfigId);
        Assert.NotNull(payload.CachedConfig);
    }

    [Fact]
    public void TryDeserializeImportPayload_PublicInviteReturnsAppMetadata()
    {
        var invite = CreateCachedInvite("invite-1", "app-1").Config;

        var result = PairingConfigExchange.TryDeserializeImportPayload(
            PublicPairingConfigJson.Serialize(invite, indented: true));

        Assert.True(result.IsSuccess, result.Message);
        var payload = Assert.IsType<PairingConfigImportPayload>(result.Payload);
        Assert.Equal(PairingConfigImportKind.PublicAppConfig, payload.Kind);
        Assert.Equal("app-1", payload.AppId);
        Assert.Equal("Example App", payload.AppName);
        Assert.Equal("invite-1", payload.SourceConfigId);
        Assert.Null(payload.CachedConfig);
    }

    [Fact]
    public void TryDeserializeImportPayload_InviteDocumentReturnsAppMetadata()
    {
        var invite = CreateCachedInvite("invite-1", "app-1").Config;
        var document = PairingConfigDocumentFactory.Create(
            invite,
            ["127.0.0.1"],
            "Host",
            "Test Wi-Fi",
            "unit-test");

        var result = PairingConfigExchange.TryDeserializeImportPayload(
            PairingConfigDocumentFactory.Serialize(document));

        Assert.True(result.IsSuccess, result.Message);
        var payload = Assert.IsType<PairingConfigImportPayload>(result.Payload);
        Assert.Equal(PairingConfigImportKind.PublicAppConfig, payload.Kind);
        Assert.Equal("invite-1", payload.SourceConfigId);
    }

    [Fact]
    public void CompactJsonExport_WritesCurrentInviteShape()
    {
        var invite = CreateCachedInvite("invite-1", "app-1").Config;

        var json = PairingConfigCompactJsonExport.Serialize(invite);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(PairingConfig.SchemaName, root.GetProperty("schema").GetString());
        Assert.Equal("invite-1", root.GetProperty("inviteId").GetString());
        Assert.Equal("app-1", root.GetProperty("appId").GetString());
        Assert.Equal("ws", root.GetProperty("allowedTransports")[0].GetString());
        Assert.True(root.GetProperty("enrollment").TryGetProperty("accessToken", out _));
        Assert.False(root.TryGetProperty("configId", out _));
        Assert.False(root.TryGetProperty("challenge", out _));
        Assert.Contains(Environment.NewLine, json, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactCode_RoundTripsCurrentInviteDocument()
    {
        var invite = CreateCachedInvite("invite-1", "app-1").Config;
        var sourceDocument = PairingConfigDocumentFactory.Create(
            invite,
            ["192.168.0.23"],
            "Host",
            "Test Wi-Fi",
            "unit-test",
            DateTimeOffset.Parse("2026-07-21T00:00:00Z"));

        var compactCode = PairingConfigDocumentFactory.SerializeCompactCode(sourceDocument);
        var json = DecodeCompactCode(compactCode);
        using var document = JsonDocument.Parse(json);

        Assert.StartsWith("ans2:", compactCode, StringComparison.Ordinal);
        Assert.Equal(
            "ansight.enrollment-invite-document.v2",
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(
            "invite-1",
            document.RootElement.GetProperty("invite").GetProperty("inviteId").GetString());
        Assert.Equal(
            "192.168.0.23",
            document.RootElement.GetProperty("discovery").GetProperty("hostAddresses")[0].GetString());

        Assert.True(
            PairingConfigCompactJsonExport.TrySerializeCompactCode(
                compactCode,
                "invite-1",
                out var directInviteJson));
        using var directInvite = JsonDocument.Parse(directInviteJson);
        Assert.Equal("invite-1", directInvite.RootElement.GetProperty("inviteId").GetString());
    }

    private static CachedPairingConfig CreateCachedInvite(string inviteId, string appId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CachedPairingConfig
        {
            Config = new PairingConfig
            {
                Schema = PairingConfig.SchemaName,
                ConfigId = inviteId,
                AppId = appId,
                AppName = "Example App",
                IssuedAt = now.AddMinutes(-5),
                ExpiresAt = now.AddMinutes(30),
                MinProtocolVersion = 2,
                AllowedTransports = [PairingTransportNames.Ws],
                Host = new PairingHost
                {
                    HostId = "host-1",
                    HostName = "Host",
                    DiscoveryPort = ProtocolDefaults.DiscoveryPort
                },
                Enrollment = new PairingEnrollment
                {
                    Secret = CryptoUtil.CreateBase64UrlRandom(32),
                    ExpiresAt = now.AddMinutes(30),
                    GrantExpiresAt = now.AddDays(14),
                    MaxUses = 1,
                    MaxToolPolicy = "read"
                }
            },
            Consumed = false
        };
    }

    private static string DecodeCompactCode(string compactCode)
    {
        var separatorIndex = compactCode.IndexOf(':');
        Assert.True(separatorIndex > 0);
        var compressedBytes = CryptoUtil.FromBase64Url(compactCode[(separatorIndex + 1)..]);
        using var compressedStream = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var jsonStream = new MemoryStream();
        gzip.CopyTo(jsonStream);
        return Encoding.UTF8.GetString(jsonStream.ToArray());
    }
}
