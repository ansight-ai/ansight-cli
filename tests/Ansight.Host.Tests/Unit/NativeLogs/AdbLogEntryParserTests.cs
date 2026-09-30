using Ansight.Adb;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class AdbLogEntryParserTests
{
    [Fact]
    public void TryParse_ParsesUtcThreadTimeLogcatEntry()
    {
        var parsed = AdbLogEntryParser.TryParse(
            "2026-07-14 03:04:05.123456 +0000  1234  5678 E ExampleTag: Something failed",
            out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T03:04:05.123456Z"), entry!.TimestampUtc);
        Assert.Equal(1234, entry.ProcessId);
        Assert.Equal(5678, entry.ThreadId);
        Assert.Equal(AdbLogPriority.Error, entry.Priority);
        Assert.Equal("ExampleTag", entry.Tag);
        Assert.Equal("Something failed", entry.Message);
    }

    [Fact]
    public void TryParse_ParsesPixelUtcLogcatEntry()
    {
        var parsed = AdbLogEntryParser.TryParse(
            "2026-08-19 08:57:46.582280 +0000  1250  1250 D WifiHAL : Start debug command",
            out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse("2026-08-19T08:57:46.582280Z"), entry!.TimestampUtc);
        Assert.Equal(1250, entry.ProcessId);
        Assert.Equal(1250, entry.ThreadId);
        Assert.Equal(AdbLogPriority.Debug, entry.Priority);
        Assert.Equal("WifiHAL", entry.Tag);
        Assert.Equal("Start debug command", entry.Message);
    }

    [Theory]
    [InlineData("2026-07-14 03:04:05 1 2 I Tag: message", "2026-07-14T03:04:05Z")]
    [InlineData("2026-07-14 03:04:05.1 1 2 W Tag:message", "2026-07-14T03:04:05.1Z")]
    [InlineData("2026-07-14 03:04:05.1234567 1 2 D Tag: message", "2026-07-14T03:04:05.1234567Z")]
    public void TryParse_AcceptsSupportedTimestampPrecision(string line, string expectedTimestamp)
    {
        var parsed = AdbLogEntryParser.TryParse(line, out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse(expectedTimestamp), entry!.TimestampUtc);
    }

    [Theory]
    [InlineData("2026-08-19 18:57:46.582280 +1000 24567 24580 I Redpoint: message", "2026-08-19T08:57:46.582280Z")]
    [InlineData("2026-08-19 01:57:46.582280 -07:00 24567 24580 I Redpoint: message", "2026-08-19T08:57:46.582280Z")]
    public void TryParse_ConvertsExplicitOffsetToUtc(string line, string expectedTimestamp)
    {
        var parsed = AdbLogEntryParser.TryParse(line, out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse(expectedTimestamp), entry!.TimestampUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--------- beginning of main")]
    [InlineData("2026-99-14 03:04:05 1 2 I Tag: invalid date")]
    public void TryParse_RejectsNonEntries(string line)
    {
        Assert.False(AdbLogEntryParser.TryParse(line, out var entry));
        Assert.Null(entry);
    }
}
