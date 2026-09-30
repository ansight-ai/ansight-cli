using Ansight.Host.Audio.Android;

namespace Ansight.Host.Tests.Unit.Audio;

public sealed class AndroidMicrophoneParserTests
{
    internal const string ActiveDump = """
        Now:         09-08 16:54:23.944
        Output thread output, name AudioOut, type 0 (MIXER):
          Standby: no
          Signal power history (resolution: 50.0 ms):
           09-08 16:54:23.750: [ -3 -3 -3 -3
          1 Tracks of which 1 are active
        Input thread microphone, name AudioIn_36, tid 4257, type 3 (RECORD):
          Standby: no
          Input device: 0x80000004 (AUDIO_DEVICE_IN_BUILTIN_MIC)
          Last read occurred (msecs): 22
          Frames read: 4096
          Signal power history (resolution: 50.0 ms):
           09-08 16:54:23.750: [ -88.3 -77.5 -77.5 -77.2
          1 Tracks of which 1 are active
        """;

    [Fact]
    public void FindsRecentQuietMicrophoneAndIgnoresLoudOutput()
    {
        var result = AndroidMicrophoneParser.Parse(ActiveDump);
        Assert.True(result.IsReady);
        Assert.Equal(-77.2, result.MaximumPowerDb);
        Assert.Equal(22, result.LastReadAgeMs);
    }

    [Fact]
    public void LiveContinuationRowsReportUnhealthyInputInsteadOfStaleHistory()
    {
        var result = AndroidMicrophoneParser.Parse(AndroidAudioFlingerFixtures.UnhealthyContinuation);
        Assert.Equal(AndroidMicrophoneState.Unhealthy, result.State);
        Assert.Equal("emulator-input-unhealthy", result.Code);
        Assert.Equal(-3, result.MaximumPowerDb);
        Assert.Equal(15, result.LastReadAgeMs);
    }

    [Fact]
    public void QuietContinuationRowsUseTheirOwnResolutionAndLatestTimestamp()
    {
        var result = AndroidMicrophoneParser.Parse(AndroidAudioFlingerFixtures.QuietContinuation);
        Assert.True(result.IsReady);
        Assert.Equal(-77.5, result.MaximumPowerDb);
        Assert.Equal(15, result.LastReadAgeMs);
    }

    [Theory]
    [InlineData("09-08 18:10:36.481", "09-08 18:10:34.481")]
    [InlineData("09-08 18:10:36.481", "09-08 18:10:38.481")]
    [InlineData("resolution: 50.0 ms", "resolution: invalid ms")]
    [InlineData("09-08 18:10:36.481:     -77.5", "09-08 18:10:36.481:     invalid")]
    [InlineData("09-08 18:10:36.481:     -77.5", "09-08 18:10:36.481:     NaN")]
    public void UnverifiableLatestContinuationCannotFallBackToEarlierQuietRows(string search, string replacement)
    {
        var dump = AndroidAudioFlingerFixtures.QuietContinuation.Replace(search, replacement, StringComparison.Ordinal);
        Assert.Equal(AndroidMicrophoneState.Unknown, AndroidMicrophoneParser.Parse(dump).State);
    }

    [Fact]
    public void UnrelatedTimestampedLogCannotOverrideUnhealthySignalHistory()
    {
        var dump = AndroidAudioFlingerFixtures.UnhealthyContinuation.Replace("09-08 18:10:11.000 AT::add (redacted)", "09-08 18:10:36.481: -77.5 -77.5", StringComparison.Ordinal);
        Assert.Equal(AndroidMicrophoneState.Unhealthy, AndroidMicrophoneParser.Parse(dump).State);
    }

    [Fact]
    public void EmptyLatestHistoryCannotFallBackToEarlierQuietSamples()
    {
        var dump = ActiveDump + "\n  Signal power history (resolution: 50.0 ms):\n";
        Assert.Equal(AndroidMicrophoneState.Unknown, AndroidMicrophoneParser.Parse(dump).State);
    }

    [Fact]
    public void CompletedContinuationRowMayHaveClosingBracket()
    {
        var dump = AndroidAudioFlingerFixtures.QuietContinuation.Replace("-77.5\n\n", "-77.5 ]\n\n", StringComparison.Ordinal);
        Assert.True(AndroidMicrophoneParser.Parse(dump).IsReady);
    }

    [Theory]
    [InlineData("Frames read: 4096", "Frames read: 0", "Unknown")]
    [InlineData("Last read occurred (msecs): 22", "Last read occurred (msecs): 500", "NotReady")]
    [InlineData("AUDIO_DEVICE_IN_BUILTIN_MIC", "AUDIO_DEVICE_IN_REMOTE_SUBMIX", "Unknown")]
    [InlineData("-88.3 -77.5 -77.5 -77.2", "-3 -3 -3 -3", "Unhealthy")]
    [InlineData("-88.3 -77.5 -77.5 -77.2", "", "Unknown")]
    [InlineData("1 Tracks of which 1 are active", "2 Tracks of which 2 are active", "Unknown")]
    [InlineData("09-08 16:54:23.750", "09-08 16:54:03.750", "Unknown")]
    public void RefusesUnprovenOrUnhealthyInput(string search, string replacement, string expected)
        => Assert.Equal(expected, AndroidMicrophoneParser.Parse(ActiveDump.Replace(search, replacement, StringComparison.Ordinal)).State.ToString());

    [Fact]
    public void AcceptsKnownDigitalSilence()
        => Assert.True(AndroidMicrophoneParser.Parse(ActiveDump.Replace("-88.3 -77.5 -77.5 -77.2", "-inf -inf -inf -inf", StringComparison.Ordinal)).IsReady);

    [Fact]
    public void DoesNotInferCurrentInputDeviceFromLocalHistory()
    {
        var dump = ActiveDump.Replace("AUDIO_DEVICE_IN_BUILTIN_MIC", "AUDIO_DEVICE_IN_REMOTE_SUBMIX", StringComparison.Ordinal)
            + "\n  Local log: Previously used (AUDIO_DEVICE_IN_BUILTIN_MIC)\n";
        Assert.Equal(AndroidMicrophoneState.Unknown, AndroidMicrophoneParser.Parse(dump).State);
    }

    [Fact]
    public void AcceptsCarriageReturnsFromHostOutput()
        => Assert.True(AndroidMicrophoneParser.Parse(ActiveDump.Replace("\n", "\r\n", StringComparison.Ordinal)).IsReady);

    [Fact]
    public void IgnoresHistoricalRecordingThreads()
    {
        var inactive = ActiveDump.Replace("Standby: no", "Standby: yes", StringComparison.Ordinal);
        Assert.Equal(AndroidMicrophoneState.NotReady, AndroidMicrophoneParser.Parse(inactive + "\nHistorical Thread Log\n- " + ActiveDump.Replace("\n", "\n- ", StringComparison.Ordinal)).State);
    }

    [Fact]
    public void UnknownOutputCannotAuthorizeInjection()
        => Assert.Equal(AndroidMicrophoneState.Unknown, AndroidMicrophoneParser.Parse("Permission denied").State);
}
