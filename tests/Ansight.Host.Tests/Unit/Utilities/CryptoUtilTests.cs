using System.Text.RegularExpressions;

namespace Ansight.Host.Tests.Unit.Utilities;

public sealed class CryptoUtilTests
{
    [Fact]
    public void Sha256Hex_ReturnsExpectedDigestForKnownPayload()
    {
        var digest = CryptoUtil.Sha256Hex(Encoding.UTF8.GetBytes("hello"));

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", digest);
    }

    [Fact]
    public void CreateBase64UrlRandom_ReturnsUrlSafeTokenWithoutPadding()
    {
        var token = CryptoUtil.CreateBase64UrlRandom(32);

        Assert.DoesNotContain("=", token, StringComparison.Ordinal);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), token);
        Assert.True(token.Length >= 43);
    }
}
