using Ansight.SimCtl;
using System.Text;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class SimCtlLogEntryParserTests
{
    [Fact]
    public void TryParse_ParsesUnifiedLogNdjsonEntryAndNormalizesTimezone()
    {
        const string line = """
            {"timestamp":"2026-07-14 13:04:05.123456+1000","processID":4321,"threadID":9876,"messageType":"Fault","subsystem":"com.example.app","category":"network","processImagePath":"/Example.app/Example","eventMessage":"Request failed"}
            """;

        var parsed = SimCtlLogEntryParser.TryParse(line, out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T03:04:05.123456Z"), entry!.TimestampUtc);
        Assert.Equal(4321, entry.ProcessId);
        Assert.Equal(9876, entry.ThreadId);
        Assert.Equal(SimCtlLogPriority.Fault, entry.Priority);
        Assert.Equal("com.example.app", entry.Subsystem);
        Assert.Equal("network", entry.Category);
        Assert.Equal("Request failed", entry.Message);
    }

    [Fact]
    public void TryParse_ParsesUtf8InputWithColonOffsetAndEscapedMessage()
    {
        const string line = """
            {"ignored":{"nested":true},"timestamp":"2026-07-14 13:04:05.123+10:00","processID":4321,"messageType":"info","eventMessage":"Line one\nLine two"}
            """;

        var parsed = SimCtlLogEntryParser.TryParse(Encoding.UTF8.GetBytes(line), out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T03:04:05.123Z"), entry!.TimestampUtc);
        Assert.Equal(SimCtlLogPriority.Information, entry.Priority);
        Assert.Equal("Line one\nLine two", entry.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"timestamp\":\"2026-07-14 13:04:05+1000\"}")]
    public void TryParse_RejectsInvalidOrIncompleteEntries(string line)
    {
        Assert.False(SimCtlLogEntryParser.TryParse(line, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void ProcessInfoDetection_RecognizesMatchingMissingAndWrongSimulatorProcesses()
    {
        const string deviceUdid = "AC42C25B-229C-4EAC-B7F7-3BC888988022";
        var existing = new SimCtlCommandResult(
            0,
            $"program path = /Applications/Example.app/Example\n\tSIMULATOR_UDID => {deviceUdid}",
            string.Empty);
        var missing = new SimCtlCommandResult(
            0,
            "Could not get proc info PID 99999: 3: No such process\nprogram path = (could not resolve path)",
            string.Empty);
        var wrongSimulator = new SimCtlCommandResult(
            0,
            "program path = /Applications/Example.app/Example\n\tSIMULATOR_UDID => C868C8C0-F2DE-4C66-A337-142401E11B35",
            string.Empty);

        Assert.True(SimCtlClient.IsProcessInfoAvailable(existing, deviceUdid));
        Assert.False(SimCtlClient.IsProcessInfoAvailable(missing, deviceUdid));
        Assert.False(SimCtlClient.IsProcessInfoAvailable(wrongSimulator, deviceUdid));
    }
}
