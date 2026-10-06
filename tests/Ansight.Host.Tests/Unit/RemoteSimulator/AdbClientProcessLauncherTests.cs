using System.Text;
using Ansight.Adb;
using Ansight.Host.Devices.Motion;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AdbClientProcessLauncherTests
{
    [Fact]
    public async Task MotionSequence_RestoresOriginalAccelerationAfterSamples()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "acceleration = 0:9.81:0\nOK\n", "OK\n", "OK\n", "OK\n");
        var player = new AndroidEmulatorMotionPlayer(new AdbClient("/fake/adb", launcher));

        await player.PlayAsync("emulator-5554",
        [
            new DeviceMotionSample(new EmulatorAcceleration(20, 0, 0), 10),
            new DeviceMotionSample(new EmulatorAcceleration(-20, 0, 0), 10)
        ]);

        Assert.Equal(4, launcher.Requests.Count);
        Assert.Equal(["-s", "emulator-5554", "emu", "sensor", "get", "acceleration"],
            launcher.Requests[0].Arguments);
        Assert.Equal("20:0:0", launcher.Requests[1].Arguments[^1]);
        Assert.Equal("-20:0:0", launcher.Requests[2].Arguments[^1]);
        Assert.Equal("0:9.81:0", launcher.Requests[3].Arguments[^1]);
    }

    [Fact]
    public async Task MotionSequence_RejectsPhysicalDeviceBeforeSendingCommands()
    {
        var launcher = new RecordingAdbProcessLauncher();
        var player = new AndroidEmulatorMotionPlayer(new AdbClient("/fake/adb", launcher));

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => player.PlayAsync(
            "physical-device", [new DeviceMotionSample(new EmulatorAcceleration(1, 2, 3), 10)]));

        Assert.Empty(launcher.Requests);
    }

    [Fact]
    public async Task MotionSequence_RestoresOriginalAccelerationAfterCancellation()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "acceleration = 0:9.81:0\nOK\n", "OK\n", "OK\n");
        var player = new AndroidEmulatorMotionPlayer(new AdbClient("/fake/adb", launcher));
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.PlayAsync(
            "emulator-5554", [new DeviceMotionSample(new EmulatorAcceleration(20, 0, 0), 1000)],
            cancellation.Token));

        Assert.Equal(3, launcher.Requests.Count);
        Assert.Equal("0:9.81:0", launcher.Requests[2].Arguments[^1]);
    }
    [Fact]
    public async Task WaitForEmulatorBootAsync_WaitsForMatchingAvdAndPackageManager()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "List of devices attached\nemulator-5554 device\nemulator-5556 device\n",
            "Other_AVD\nOK\n", "Demo_AVD\nOK\n", "0\n",
            "List of devices attached\nemulator-5556 device\n",
            "Demo_AVD\nOK\n", "1\n", "package:/system/framework/framework-res.apk\n");
        var client = new AdbClient("/fake/adb", launcher);

        var serial = await client.WaitForEmulatorBootAsync("Demo_AVD", TimeSpan.FromSeconds(5));

        Assert.Equal("emulator-5556", serial);
        Assert.Equal(["-s", "emulator-5556", "shell", "pm", "path", "android"], launcher.LastRequest!.Arguments);
    }

    [Fact]
    public async Task WaitForEmulatorBootAsync_TimesOutWhenNoMatchingDeviceAppears()
    {
        var client = new AdbClient("/fake/adb", new RecordingAdbProcessLauncher());
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.WaitForEmulatorBootAsync("Missing_AVD", TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task WaitForEmulatorBootAsync_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new AdbClient("/fake/adb", new RecordingAdbProcessLauncher());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WaitForEmulatorBootAsync("Demo_AVD", TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public async Task CollapseSystemUiPanelsAsync_UsesStatusBarServiceForExactDevice()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.CollapseSystemUiPanelsAsync("device-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["-s", "device-1", "shell", "cmd", "statusbar", "collapse"],
            launcher.LastRequest?.Arguments);
    }

    [Theory]
    [InlineData("  mInputShown=true\n", true)]
    [InlineData("  mInputShown=false\n", false)]
    [InlineData("      mInputShown=true\n", true)]
    [InlineData("      mInputShown=false\n", false)]
    [InlineData("\tmInputShown=true\n", true)]
    public async Task GetSoftwareKeyboardVisibilityAsync_ReadsInputMethodServiceState(
        string output,
        bool expected)
    {
        var launcher = new RecordingAdbProcessLauncher(output);
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.GetSoftwareKeyboardVisibilityAsync("device-1");

        Assert.Equal(expected, result);
        Assert.Equal(
            [
                "-s",
                "device-1",
                "shell",
                "dumpsys input_method | grep '^[[:space:]]*mInputShown='"
            ],
            launcher.LastRequest?.Arguments);
    }

    [Fact]
    public async Task GetSoftwareKeyboardVisibilityAsync_DoesNotUseStaleInputViewState()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "mInputShown=false\n  mIsInputViewShown=true\n");
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.GetSoftwareKeyboardVisibilityAsync("device-1");

        Assert.False(result);
    }

    [Fact]
    public async Task GetSoftwareKeyboardVisibilityAsync_WhenStateIsUnavailable_ReturnsNull()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.GetSoftwareKeyboardVisibilityAsync("device-1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDevicesAsync_UsesInjectedProcessLauncher()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "List of devices attached\n"
            + "emulator-5554 device product:sdk_phone model:Pixel_9 device:emu64xa transport_id:1\n");
        var client = new AdbClient("/fake/adb", launcher);

        var devices = await client.GetDevicesAsync();

        var device = Assert.Single(devices);
        Assert.Equal("emulator-5554", device.Serial);
        Assert.Equal("Pixel 9", device.Model);
        Assert.Equal(["devices", "-l"], launcher.LastRequest?.Arguments);
        Assert.True(launcher.Process?.StandardInputWasDisposed);
        Assert.True(launcher.Process?.WasDisposed);
    }

    [Fact]
    public async Task GetEmulatorAvdNameAsync_ReadsConsoleNameWithoutProtocolTerminator()
    {
        var launcher = new RecordingAdbProcessLauncher("Pixel_9a\nOK\n");
        var client = new AdbClient("/fake/adb", launcher);

        var avdName = await client.GetEmulatorAvdNameAsync("emulator-5554");

        Assert.Equal("Pixel_9a", avdName);
        Assert.Equal(
            ["-s", "emulator-5554", "emu", "avd", "name"],
            launcher.LastRequest?.Arguments);
    }

    [Theory]
    [InlineData("nosdcard\n", "Physical size: 1080x2424\n", "Physical density: 420\n", AndroidDeviceFormFactor.Phone)]
    [InlineData("tablet\n", "Physical size: 1600x2560\n", "Physical density: 320\n", AndroidDeviceFormFactor.Tablet)]
    [InlineData("tv\n", "Physical size: 1920x1080\n", "Physical density: 160\n", AndroidDeviceFormFactor.Unknown)]
    public async Task GetDeviceFormFactorAsync_UsesCharacteristicsAndDisplayMetrics(
        string characteristics,
        string size,
        string density,
        AndroidDeviceFormFactor expected)
    {
        var launcher = new RecordingAdbProcessLauncher(characteristics, size, density);
        var client = new AdbClient("/fake/adb", launcher);

        var formFactor = await client.GetDeviceFormFactorAsync("device-1");

        Assert.Equal(expected, formFactor);
        Assert.Equal(3, launcher.Requests.Count);
        Assert.Equal(
            ["-s", "device-1", "shell", "getprop", "ro.build.characteristics"],
            launcher.Requests[0].Arguments);
        Assert.Equal(["-s", "device-1", "shell", "wm", "size"], launcher.Requests[1].Arguments);
        Assert.Equal(["-s", "device-1", "shell", "wm", "density"], launcher.Requests[2].Arguments);
    }

    [Fact]
    public async Task SetEmulatorLocationAsync_UsesLongitudeLatitudeConsoleOrder()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        await client.SetEmulatorLocationAsync("emulator-5554", -33.8688d, 151.2093d);

        Assert.Equal(
            ["-s", "emulator-5554", "emu", "geo", "fix", "151.2093", "-33.8688"],
            launcher.LastRequest?.Arguments);
    }

    [Fact]
    public async Task GetInstalledPackageSha256Async_HashesInstalledBaseApk()
    {
        const string checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string packagePath = "/data/app/example/base.apk";
        var launcher = new RecordingAdbProcessLauncher(
            $"package:{packagePath}\npackage:/data/app/example/split_config.arm64_v8a.apk\n",
            $"{checksum.ToUpperInvariant()}  {packagePath}\n");
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.GetInstalledPackageSha256Async("device-1", "com.example.app");

        Assert.Equal(checksum, result);
        Assert.Equal(
            ["-s", "device-1", "shell", "pm", "path", "com.example.app"],
            launcher.Requests[0].Arguments);
        Assert.Equal(
            ["-s", "device-1", "shell", "sha256sum", packagePath],
            launcher.Requests[1].Arguments);
    }

    [Fact]
    public async Task GetInstalledApplicationAsync_ReadsVersionAndInstallTimes()
    {
        const string packagePath = "/data/app/example/base.apk";
        var launcher = new RecordingAdbProcessLauncher(
            $"package:{packagePath}\n",
            """
              versionCode=456 minSdk=24 targetSdk=36
              versionName=1.2.3
              firstInstallTime=2026-08-25 11:02:03
              lastUpdateTime=2026-08-26 14:05:06
            """,
            "Australia/Sydney\n");
        var client = new AdbClient("/fake/adb", launcher);

        var application = await client.GetInstalledApplicationAsync("device-1", "com.example.app");

        Assert.NotNull(application);
        Assert.Equal("1.2.3", application.Version);
        Assert.Equal("456", application.BuildVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero), application.InstalledAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 4, 5, 6, TimeSpan.Zero), application.LastUpdatedAtUtc);
        Assert.Equal(3, launcher.Requests.Count);
    }

    [Fact]
    public async Task GetInstalledPackageSha256Async_WhenPackageIsMissing_ReturnsNull()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        var result = await client.GetInstalledPackageSha256Async("device-1", "com.example.missing");

        Assert.Null(result);
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public async Task LaunchApplicationAsync_ResolvesAndStartsLauncherActivity()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "com.example.app/.MainActivity\n", "Status: ok\n");
        var client = new AdbClient("/fake/adb", launcher);

        await client.LaunchApplicationAsync("emulator-5554", "com.example.app");

        Assert.Equal(
            ["-s", "emulator-5554", "shell", "am", "start", "-W",
                "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER",
                "-n", "com.example.app/.MainActivity"],
            launcher.LastRequest?.Arguments);
    }

    [Fact]
    public async Task LaunchApplicationAsync_ReportsMissingPackageLauncherActivity()
    {
        var launcher = new RecordingAdbProcessLauncher("No activity found\n");
        var client = new AdbClient("/fake/adb", launcher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LaunchApplicationAsync("emulator-5554", "com.example.missing"));

        Assert.Contains("could not resolve a launcher activity", exception.Message);
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public async Task LaunchApplicationAsync_ReportsActivityManagerErrorDespiteZeroExitCode()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "com.example.app/.MainActivity\n", "Error: Activity class does not exist.\n");
        var client = new AdbClient("/fake/adb", launcher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LaunchApplicationAsync("emulator-5554", "com.example.app"));
    }

    [Fact]
    public async Task LaunchApplicationForProfilingAsync_UsesResolvedColdStartActivity()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "com.example.app/.MainActivity\n",
            "Status: ok\nTotalTime: 123\n");
        var client = new AdbClient("/fake/adb", launcher);

        await client.LaunchApplicationForProfilingAsync("emulator-5554", "com.example.app");

        Assert.Equal(2, launcher.Requests.Count);
        Assert.Equal(
            [
                "-s",
                "emulator-5554",
                "shell",
                "am",
                "start",
                "-W",
                "-S",
                "-a",
                "android.intent.action.MAIN",
                "-c",
                "android.intent.category.LAUNCHER",
                "-n",
                "com.example.app/.MainActivity"
            ],
            launcher.LastRequest?.Arguments);
    }

    [Fact]
    public async Task LaunchApplicationAsync_WithIntentExtra_ResolvesActivityAndUsesStdin()
    {
        var launcher = new RecordingAdbProcessLauncher(
            "com.example.app/.MainActivity\n",
            "Status: ok\n");
        var client = new AdbClient("/fake/adb", launcher);

        await client.LaunchApplicationAsync(
            "device-1",
            "com.example.app",
            "ai.ansight.bootstrap.payload",
            "ans2:sensitive-one-use-payload");

        Assert.Equal(
            [
                "-s",
                "device-1",
                "shell",
                "cmd",
                "package",
                "resolve-activity",
                "--brief",
                "--components",
                "-a",
                "android.intent.action.MAIN",
                "-c",
                "android.intent.category.LAUNCHER",
                "-p",
                "com.example.app"
            ],
            launcher.Requests[0].Arguments);
        Assert.Equal(["-s", "device-1", "shell"], launcher.LastRequest?.Arguments);
        Assert.DoesNotContain(
            "sensitive-one-use-payload",
            string.Join(' ', launcher.LastRequest?.Arguments ?? []),
            StringComparison.Ordinal);
        Assert.Contains(
            "am start -W -S -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n 'com.example.app/.MainActivity'",
            launcher.Process?.StandardInputText,
            StringComparison.Ordinal);
        Assert.Contains(
            "--es 'ai.ansight.bootstrap.payload' 'ans2:sensitive-one-use-payload'",
            launcher.Process?.StandardInputText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchApplicationAsync_WithIntentExtra_ReportsMissingLauncherActivity()
    {
        var launcher = new RecordingAdbProcessLauncher("No activity found\n");
        var client = new AdbClient("/fake/adb", launcher);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LaunchApplicationAsync(
                "device-1",
                "com.example.missing",
                "ai.ansight.bootstrap.payload",
                "ans2:sensitive-one-use-payload"));

        Assert.Contains("could not resolve a launcher activity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-one-use-payload", exception.Message, StringComparison.Ordinal);
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public async Task InstallPackageAsync_ReplacesExistingPackage()
    {
        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-adb-install-{Guid.NewGuid():N}.apk");
        await File.WriteAllTextAsync(packagePath, "fixture");
        try
        {
            var launcher = new RecordingAdbProcessLauncher("Success\n");
            var client = new AdbClient("/fake/adb", launcher);

            await client.InstallPackageAsync("emulator-5554", packagePath);

            Assert.Equal(
                ["-s", "emulator-5554", "install", "-r", packagePath],
                launcher.LastRequest?.Arguments);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task TerminateApplicationAsync_UsesForceStop()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        await client.TerminateApplicationAsync("emulator-5554", "com.example.app");

        Assert.Equal(
            ["-s", "emulator-5554", "shell", "am", "force-stop", "com.example.app"],
            launcher.LastRequest?.Arguments);
    }

    [Fact]
    public async Task RunWithStandardInputAsync_KeepsInputOutOfProcessArguments()
    {
        var launcher = new RecordingAdbProcessLauncher(string.Empty);
        var client = new AdbClient("/fake/adb", launcher);

        await client.RunWithStandardInputAsync(
            ["-s", "emulator-5554", "shell"],
            "input 'text' 'sensitive-value'\n");

        Assert.Equal(["-s", "emulator-5554", "shell"], launcher.LastRequest?.Arguments);
        Assert.DoesNotContain(
            "sensitive-value",
            string.Join(' ', launcher.LastRequest?.Arguments ?? []),
            StringComparison.Ordinal);
        Assert.Equal(
            "input 'text' 'sensitive-value'\n",
            launcher.Process?.StandardInputText);
    }

    private sealed class RecordingAdbProcessLauncher : IAdbProcessLauncher
    {
        private readonly Queue<string> standardOutputs;

        public RecordingAdbProcessLauncher(params string[] standardOutputs)
        {
            this.standardOutputs = new Queue<string>(standardOutputs);
        }

        public List<AdbProcessStartRequest> Requests { get; } = [];

        public AdbProcessStartRequest? LastRequest { get; private set; }

        public RecordingAdbProcess? Process { get; private set; }

        public IAdbProcess Start(AdbProcessStartRequest request)
        {
            Requests.Add(request);
            LastRequest = request;
            var standardOutput = standardOutputs.Count > 0
                ? standardOutputs.Dequeue()
                : string.Empty;
            Process = new RecordingAdbProcess(standardOutput);
            return Process;
        }
    }

    private sealed class RecordingAdbProcess : IAdbProcess
    {
        private readonly TrackingMemoryStream standardInput = new();

        public RecordingAdbProcess(string standardOutput)
        {
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(standardOutput));
        }

        public int ProcessId => 1234;

        public Stream StandardInput => standardInput;

        public Stream StandardOutput { get; }

        public Stream StandardError { get; } = new MemoryStream();

        public Task<int> Completion { get; } = Task.FromResult(0);

        public bool StandardInputWasDisposed => standardInput.WasDisposed;

        public string StandardInputText => Encoding.UTF8.GetString(standardInput.ToArray());

        public bool WasDisposed { get; private set; }

        public void Terminate(bool force = false)
        {
        }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
            standardInput.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
