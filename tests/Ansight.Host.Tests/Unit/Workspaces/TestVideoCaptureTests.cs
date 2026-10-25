using System.Diagnostics;
using Ansight.Host.Sessions.Video;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class TestVideoCaptureTests
{
    [Fact]
    public void OmittedVideoSettingsDefaultToAlways()
    {
        var yaml = WorkspaceTestCatalog.Parse("/tmp/tests", "/tmp/tests/test.yaml",
            "appId: example\ntaskId: example\n");
        var json = WorkspaceTestCatalog.Parse("/tmp/tests", "/tmp/tests/test.json",
            """{"appId":"example","taskId":"example"}""");
        Assert.Equal("always", new TestVideoOptions().Retention);
        Assert.Equal("always", yaml.Video.Retention);
        Assert.Equal(yaml.Video, json.Video);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("always")]
    [InlineData("on-failure")]
    public void CatalogReadsVideoRetentionInYamlAndJson(string retention)
    {
        var yaml = WorkspaceTestCatalog.Parse("/tmp/tests", "/tmp/tests/test.yaml",
            $"appId: example\ntaskId: example\nvideo:\n  retention: {retention}\n");
        var json = WorkspaceTestCatalog.Parse("/tmp/tests", "/tmp/tests/test.json",
            JsonSerializer.Serialize(new { appId = "example", taskId = "example", video = new { retention } }));
        Assert.Equal(retention, yaml.Video.Retention);
        Assert.Equal(yaml.Video, json.Video);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"retention\":\"never\"}")]
    [InlineData("{\"retention\":\"always\",\"audio\":true}")]
    public void InvalidVideoOptionsAreRejected(string video)
        => Assert.Throws<InvalidDataException>(() => WorkspaceTestCatalog.Parse("/tmp/tests", "/tmp/tests/test.json",
            "{\"appId\":\"example\",\"taskId\":\"example\",\"video\":" + video + "}"));

    [Theory]
    [InlineData(true, "always", true, "completed")]
    [InlineData(false, "on-failure", true, "completed")]
    [InlineData(true, "on-failure", true, "discarded")]
    [InlineData(false, "always", false, "partial")]
    public async Task FinalizesOnceAndRetainsAccordingToOutcome(bool passed, string retention, bool complete, string status)
    {
        var recorder = new FakeTestVideoRecorder { Complete = complete };
        SessionVideoRecording? saved = null;
        var capture = await TestVideoCapture.StartAsync("run-1", "session-1", "device-1", "android", true,
            new(retention), recorder, (snapshot, path) =>
            {
                saved = snapshot;
                Assert.True(File.Exists(path));
                Assert.Equal(2, SessionVideoMp4TimingInspector.ReadPresentationTimesUs(path).Count);
                Assert.Equal("run-1", snapshot.RunId);
                return OperationResult.Success("Saved");
            }, CancellationToken.None);
        var result = await capture.CompleteAsync(passed);
        Assert.Equal(result, await capture.CompleteAsync(!passed));
        Assert.Equal(1, recorder.Stops);
        Assert.Equal(status, result.Status);
        Assert.Equal(status != "discarded", saved is not null);
        Assert.Equal("session-1", result.SessionId);
        Assert.False(Directory.Exists(Path.GetDirectoryName(recorder.OutputPath)));
    }

    [Fact]
    public async Task CanStopTheDeviceBeforeSavingAndStillFinalizeOnlyOnce()
    {
        var recorder = new FakeTestVideoRecorder();
        var saved = false;
        var capture = await TestVideoCapture.StartAsync("run", "session", "device", "android", true,
            new("always"), recorder, (_, _) => { saved = true; return OperationResult.Success("Saved"); }, default);
        Assert.True((await capture.StopAsync()).IsComplete);
        Assert.False(saved);
        Assert.Equal(1, recorder.Stops);
        Assert.Equal("completed", (await capture.CompleteAsync(true)).Status);
        Assert.True(saved);
        Assert.Equal(1, recorder.Stops);
    }

    [Fact]
    public async Task CorruptFileIsNotPublishedAsPlayableVideo()
    {
        var recorder = new FakeTestVideoRecorder { InvalidFile = true };
        var capture = await TestVideoCapture.StartAsync("run", "session", "device", "android", true,
            new("always"), recorder, (snapshot, _) =>
            {
                Assert.Fail("Invalid video must not be published as a replay track.");
                return OperationResult.Success("Saved");
            }, CancellationToken.None);
        var result = await capture.CompleteAsync(false);
        Assert.Equal("failed", result.Status);
        Assert.Null(result.Path);
    }

    [Fact]
    public async Task UnsupportedCaptureRetainsAnExplicitStatus()
    {
        var recorder = new FakeTestVideoRecorder { StartError = new NotSupportedException("Unsupported platform") };
        var capture = await TestVideoCapture.StartAsync("run", "session", "device", "ios", false,
            new("always"), recorder, (_, _) => OperationResult.Success("Saved"), CancellationToken.None);
        var result = await capture.CompleteAsync(true);
        Assert.Equal("unsupported", result.Status);
        Assert.Null(result.Path);
        Assert.Equal(0, recorder.Stops);
    }

    [Fact]
    public async Task NativeProcessReceivesGracefulInterruptAndStopIsIdempotent()
    {
        if (OperatingSystem.IsWindows()) return;
        var start = Shell("trap 'exit 0' INT; echo 'Recording started'; while :; do sleep 0.02; done");
        var recording = await NativeTestVideoRecording.StartAsync(start, "fixture", CancellationToken.None);
        var result = await recording.StopAsync();
        Assert.True(result.IsComplete, result.Error);
        Assert.Equal(result, await recording.StopAsync());
    }

    [Fact]
    public async Task NativeProcessStartupFailureIsReported()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<IOException>(() => NativeTestVideoRecording.StartAsync(
            Shell("echo 'recorder unavailable' >&2; exit 1"), "fixture", CancellationToken.None));
    }

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        return start;
    }

    [Theory]
    [InlineData("test-recording.mp4")]
    [InlineData("test-recording-faststart.mp4")]
    public void FinalIdlePeriodExtendsDurationWithoutChangingFrameTimestamps(string fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), path);
        try
        {
            var timestamps = SessionVideoMp4TimingInspector.ReadPresentationTimesUs(path);
            SessionVideoMp4TimingInspector.ExtendFinalFrame(path, TimeSpan.FromSeconds(3));
            Assert.Equal(TimeSpan.FromSeconds(3), SessionVideoMp4TimingInspector.ReadDuration(path));
            Assert.Equal(timestamps, SessionVideoMp4TimingInspector.ReadPresentationTimesUs(path));
            var size = new FileInfo(path).Length;
            SessionVideoMp4TimingInspector.ExtendFinalFrame(path, TimeSpan.FromSeconds(2));
            Assert.Equal(size, new FileInfo(path).Length);
            Assert.Equal(TimeSpan.FromSeconds(3), SessionVideoMp4TimingInspector.ReadDuration(path));
        }
        finally { File.Delete(path); }
    }
}
