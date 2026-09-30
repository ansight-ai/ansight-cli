using Ansight.Adb;
using Ansight.Adb.Emulator;

namespace Ansight.Host.Tests.Unit.RemoteSimulator;

public sealed class AndroidVirtualDeviceClientTests
{
    private const string EmulatorPath = "/sdk/emulator/emulator";

    [Fact]
    public async Task ListAsync_ParsesAndSortsAvds()
    {
        var runner = new FakeAndroidEmulatorProcessRunner
        {
            RunResult = new AdbCommandResult(
                0,
                "Pixel_9a\nPixel_3a_API_33_arm64-v8a\nPixel_9a\n",
                string.Empty),
        };
        var client = CreateClient(runner);

        var result = await client.ListAsync();

        Assert.Equal(["Pixel_3a_API_33_arm64-v8a", "Pixel_9a"], result);
        Assert.Equal(EmulatorPath, runner.RunExecutablePath);
        Assert.Equal(["-list-avds"], runner.RunArguments);
    }

    [Fact]
    public async Task LaunchAsync_UsesStructuredAvdArguments()
    {
        var runner = new FakeAndroidEmulatorProcessRunner { ProcessId = 42 };
        var client = CreateClient(runner);

        var processId = await client.LaunchAsync("Pixel_9a");

        Assert.Equal(42, processId);
        Assert.Equal(EmulatorPath, runner.LaunchExecutablePath);
        Assert.Equal(["-avd", "Pixel_9a"], runner.LaunchArguments);
    }

    [Fact]
    public async Task LaunchAsync_HeadlessDisablesTheEmulatorWindow()
    {
        var runner = new FakeAndroidEmulatorProcessRunner { ProcessId = 42 };
        var client = CreateClient(runner);

        var processId = await client.LaunchAsync(
            "Pixel_9a",
            new AndroidVirtualDeviceLaunchOptions(Headless: true));

        Assert.Equal(42, processId);
        Assert.Equal(EmulatorPath, runner.LaunchExecutablePath);
        Assert.Equal(["-avd", "Pixel_9a", "-no-window"], runner.LaunchArguments);
    }

    [Fact]
    public async Task LaunchAsync_WhenAvdSystemImageIsMissing_ReportsSdkPackage()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ansight-android-avd-{Guid.NewGuid():N}");
        var sdkDirectory = Path.Combine(testDirectory, "sdk");
        var avdHome = Path.Combine(testDirectory, ".android", "avd");
        var avdDirectory = Path.Combine(avdHome, "Pixel_9a.avd");
        Directory.CreateDirectory(avdDirectory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(avdHome, "Pixel_9a.ini"),
                $"path={avdDirectory}\n");
            await File.WriteAllTextAsync(
                Path.Combine(avdDirectory, "config.ini"),
                "image.sysdir.1=system-images/android-36/google_apis_playstore/arm64-v8a/\n");
            var runner = new FakeAndroidEmulatorProcessRunner { ProcessId = 42 };
            var client = new AndroidVirtualDeviceClient(
                AndroidEmulatorToolResolution.Found(
                    Path.Combine(sdkDirectory, "emulator", "emulator"),
                    "test"),
                runner,
                avdHome);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.LaunchAsync("Pixel_9a"));

            Assert.Contains("system image is missing", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "system-images;android-36;google_apis_playstore;arm64-v8a",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(runner.LaunchArguments);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GetDeviceFormFactor_ReadsAvdDisplayMetrics()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ansight-android-avd-form-factor-{Guid.NewGuid():N}");
        var avdHome = Path.Combine(testDirectory, ".android", "avd");
        var phoneDirectory = Path.Combine(avdHome, "Phone.avd");
        var tabletDirectory = Path.Combine(avdHome, "Tablet.avd");
        Directory.CreateDirectory(phoneDirectory);
        Directory.CreateDirectory(tabletDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(avdHome, "Phone.ini"), $"path={phoneDirectory}\n");
            await File.WriteAllTextAsync(Path.Combine(avdHome, "Tablet.ini"), $"path={tabletDirectory}\n");
            await File.WriteAllTextAsync(
                Path.Combine(phoneDirectory, "config.ini"),
                "hw.device.name=pixel_9a\nhw.lcd.width=1080\nhw.lcd.height=2424\nhw.lcd.density=420\n");
            await File.WriteAllTextAsync(
                Path.Combine(tabletDirectory, "config.ini"),
                "hw.device.name=pixel_tablet\nhw.lcd.width=1600\nhw.lcd.height=2560\nhw.lcd.density=320\n");
            var client = new AndroidVirtualDeviceClient(
                AndroidEmulatorToolResolution.Found(EmulatorPath, "test"),
                new FakeAndroidEmulatorProcessRunner(),
                avdHome);

            Assert.Equal(AndroidDeviceFormFactor.Phone, client.GetDeviceFormFactor("Phone"));
            Assert.Equal(AndroidDeviceFormFactor.Tablet, client.GetDeviceFormFactor("Tablet"));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public void GetCandidatePaths_PrefersEmulatorBesideResolvedAdbSdk()
    {
        var executableName = OperatingSystem.IsWindows() ? "emulator.exe" : "emulator";
        var adbPath = Path.Combine(Path.DirectorySeparatorChar.ToString(), "sdk", "platform-tools", "adb");

        var firstCandidate = AndroidEmulatorToolLocator.GetCandidatePaths(adbPath).First();

        Assert.Equal(
            Path.GetFullPath(
                Path.Combine(Path.DirectorySeparatorChar.ToString(), "sdk", "emulator", executableName)),
            firstCandidate);
    }

    private static AndroidVirtualDeviceClient CreateClient(IProcessRunner runner)
        => new(
            AndroidEmulatorToolResolution.Found(EmulatorPath, "test"),
            runner,
            Path.Combine(Path.GetTempPath(), "ansight-missing-avd-home"));
}
