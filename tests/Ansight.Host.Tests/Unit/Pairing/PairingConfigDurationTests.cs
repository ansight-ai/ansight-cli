namespace Ansight.Host.Tests.Unit.Pairing;

public sealed class PairingConfigDurationTests
{
    [Theory]
    [InlineData(null, 1, 0, "1 month")]
    [InlineData("", 1, 0, "1 month")]
    [InlineData("2mo", 2, 0, "2 months")]
    [InlineData("7d", 0, 7, "7 days")]
    [InlineData("12h", 0, 0.5, "12 hours")]
    [InlineData("30m", 0, 30d / 1440d, "30 minutes")]
    public void TryParse_ParsesSupportedDurations(string? input, int expectedMonths, double expectedDays, string expectedDisplay)
    {
        var parsed = PairingConfigDuration.TryParse(input, out var duration, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(expectedMonths, duration.Months);
        Assert.Equal(TimeSpan.FromDays(expectedDays), duration.Offset);
        Assert.Equal(expectedDisplay, duration.Display);
    }

    [Theory]
    [InlineData("0d", "Duration must start with a positive number.")]
    [InlineData("7", "Missing duration unit. Try '1mo', '7d', '12h', or '30m'.")]
    [InlineData("9years", "Unsupported duration unit 'years'. Use mo, w, d, h, or m.")]
    public void TryParse_RejectsInvalidDurations(string input, string expectedError)
    {
        var parsed = PairingConfigDuration.TryParse(input, out var duration, out var error);

        Assert.False(parsed);
        Assert.Equal(expectedError, error);
        Assert.Same(PairingConfigDuration.Default, duration);
    }
}
