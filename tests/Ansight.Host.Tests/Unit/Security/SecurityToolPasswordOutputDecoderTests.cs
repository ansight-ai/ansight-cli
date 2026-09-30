namespace Ansight.Host.Tests.Unit.Security;

public sealed class SecurityToolPasswordOutputDecoderTests
{
    [Fact]
    public void Decode_WhenOutputIsPlainJson_ReturnsTrimmedJson()
    {
        var result = SecurityToolPasswordOutputDecoder.Decode("{\"value\":true}\n");

        Assert.Equal("{\"value\":true}", result);
    }

    [Fact]
    public void Decode_WhenOutputIsHexEncodedJson_ReturnsDecodedJson()
    {
        var json = "{\n  \"items\": []\n}";
        var hex = Convert.ToHexString(Encoding.UTF8.GetBytes(json)).ToLowerInvariant();

        var result = SecurityToolPasswordOutputDecoder.Decode(hex + "\n");

        Assert.Equal(json, result);
    }

    [Fact]
    public void Decode_WhenOutputIsPlainHexLikeValue_ReturnsOriginalValue()
    {
        var result = SecurityToolPasswordOutputDecoder.Decode("6465616462656566");

        Assert.Equal("6465616462656566", result);
    }
}
