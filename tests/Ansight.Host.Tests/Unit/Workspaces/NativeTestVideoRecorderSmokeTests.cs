using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Sessions.Video;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed class NativeTestVideoRecorderSmokeTests
{
    // Opt-in hardware check: records the already-selected screen without launching or controlling an app.
    [Fact]
    public async Task RecordsSelectedDeviceIntoAPlayableMp4()
    {
        var deviceId = Environment.GetEnvironmentVariable("ANSIGHT_TEST_VIDEO_DEVICE_ID");
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        var platform = Environment.GetEnvironmentVariable("ANSIGHT_TEST_VIDEO_PLATFORM") ?? "ios";
        using var environment = new TestEnvironment();
        using var runtime = environment.CreateRuntime();
        var path = Path.Combine(environment.RootPath, "native.mp4");
        var recording = await new NativeTestVideoRecorder(runtime.UserPreferences).StartAsync(
            new(platform, deviceId, true, path), CancellationToken.None);
        TestVideoRecordingCompletion result;
        try { await Task.Delay(1500); }
        finally { result = await recording.StopAsync(); }
        Assert.True(result.IsComplete, result.Error);
        SessionVideoMp4TimingInspector.ExtendFinalFrame(path, result.CapturedDuration!.Value);
        Assert.True(SessionVideoMp4TimingInspector.ReadDuration(path) >= TimeSpan.FromSeconds(1.4));
        Assert.NotEmpty(SessionVideoMp4TimingInspector.ReadPresentationTimesUs(path));
    }
}
