namespace Ansight.Cli.Tests.Commands.Session;

public sealed class ByteSizeParserTests
{
    [Theory]
    [InlineData("5GB", 5_000_000_000)]
    [InlineData("5 GiB", 5_368_709_120)]
    [InlineData("256MiB", 268_435_456)]
    [InlineData("0.5TiB", 549_755_813_888)]
    public void Parse_WithSupportedUnit_ReturnsBytes(string source, long expected)
    {
        var actual = ByteSizeParser.Parse(source, "max-cache", 0, long.MaxValue);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("5PB")]
    [InlineData("255MiB")]
    public void Parse_WithInvalidValue_ThrowsUsage(string source)
    {
        Assert.Throws<CliUsageException>(() => ByteSizeParser.Parse(
            source,
            "max-cache",
            256L * 1024L * 1024L,
            1024L * 1024L * 1024L * 1024L));
    }
}
