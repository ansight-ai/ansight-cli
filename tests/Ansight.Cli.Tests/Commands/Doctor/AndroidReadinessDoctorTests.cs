namespace Ansight.Cli.Tests.Commands.Doctor;

public sealed class AndroidReadinessDoctorTests
{
    [Fact]
    public async Task DefaultDoctorDoesNotWarnAboutAnUnrequestedAndroidConnection()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var adb = Path.Combine(directory.Path, "adb");
        await File.WriteAllTextAsync(adb, "#!/bin/sh\nprintf 'List of devices attached\\n'\n");
        File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var checks = await AndroidReadinessDoctor.CheckAsync("auto", adb, CancellationToken.None);

        Assert.Equal(["android.sdk"], checks.Select(check => check.Name));
        Assert.False(checks[0].IsRequired);
    }

    [Fact]
    public void RequiringConnectionStillSelectsTheDeviceCheck()
    {
        Assert.Equal("auto", DoctorCommand.ResolveTarget(CliArguments.Parse(["doctor"])));
        Assert.Equal("android-device", DoctorCommand.ResolveTarget(CliArguments.Parse(["doctor", "--require", "android.connection"])));
        Assert.Equal("auto", DoctorCommand.ResolveTarget(CliArguments.Parse(["doctor", "--full", "--require", "android.connection"])));
        Assert.Equal("android-emulator", DoctorCommand.ResolveTarget(CliArguments.Parse(["doctor", "--target", "android-emulator"])));
    }

    [Theory]
    [InlineData("device", true)]
    [InlineData("unauthorized", false)]
    [InlineData("offline", false)]
    public async Task ExternalTargetDoesNotRequireLocalEmulatorOrKvm(string state, bool expectedReady)
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var adb = Path.Combine(directory.Path, "adb");
        await File.WriteAllTextAsync(adb, "#!/bin/sh\nprintf 'List of devices attached\\nremote-test\\t" + state + "\\n'\n");
        File.SetUnixFileMode(adb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var checks = await AndroidReadinessDoctor.CheckAsync("android-device", adb, CancellationToken.None);
        Assert.Equal(expectedReady, checks.Single(check => check.Name == "android.connection").IsSuccess);
        Assert.All(checks, check => Assert.True(check.IsRequired));
        Assert.DoesNotContain(checks, check => check.Name is "android.kvm" or "android.emulator" or "android.system-image" or "android.acceleration");
    }

    [Theory]
    [InlineData(true, "accel:\n0\nKVM is installed and usable.\naccel", true)]
    [InlineData(true, "accel:\n6\nKVM is not installed.\naccel", false)]
    [InlineData(false, "accel:\n0\n", false)]
    [InlineData(true, "", false)]
    public void AccelerationRequiresARecognizedSuccessfulProbe(bool exitedSuccessfully, string output, bool expected)
    {
        var check = AndroidReadinessDoctor.CreateAccelerationCheck(exitedSuccessfully, output, "/sdk/emulator/emulator");
        Assert.Equal(expected, check.IsSuccess);
        Assert.True(check.IsRequired);
    }

    [Fact]
    public void MissingKvmHasActionableFailure()
    {
        using var directory = TestDirectory.Create();
        var check = AndroidReadinessDoctor.CheckKvm(Path.Combine(directory.Path, "missing-kvm"));
        Assert.False(check.IsSuccess);
        Assert.True(check.IsRequired);
        Assert.Contains("nested virtualization", check.Message);
        Assert.Contains("external Android target", check.Message);
    }

    [Fact]
    public void AvdRequiresItsActualSystemImageNotJustAnImageDirectory()
    {
        using var directory = TestDirectory.Create();
        var avdHome = Path.Combine(directory.Path, "avd");
        var avd = Path.Combine(avdHome, "Pixel.avd");
        var sdk = Path.Combine(directory.Path, "sdk");
        var image = Path.Combine(sdk, "system-images", "android-35", "default", "x86_64");
        Directory.CreateDirectory(avd);
        Directory.CreateDirectory(image);
        File.WriteAllText(Path.Combine(avd, "config.ini"), "image.sysdir.1=system-images/android-35/default/x86_64/\n");
        Assert.False(AndroidReadinessDoctor.CheckSystemImages(["Pixel"], avdHome, sdk).IsSuccess);
        File.WriteAllBytes(Path.Combine(image, "system.img"), [1]);
        Assert.True(AndroidReadinessDoctor.CheckSystemImages(["Pixel"], avdHome, sdk).IsSuccess);
        Assert.False(AndroidReadinessDoctor.CheckSystemImages([], avdHome, sdk).IsSuccess);
    }

    [Fact]
    public void RequiringAnUnsupportedPlatformCannotReturnHealthy()
    {
        var checks = new List<DoctorCheck> { new("device.ios", "not-applicable", true, false, "macOS required", null) };
        DoctorCommand.RequireChecks(checks, ["device.ios"]);
        Assert.False(checks[0].IsSuccess);
        Assert.True(checks[0].IsRequired);
        Assert.DoesNotContain("Xcode", checks[0].InstallInstructions);
    }
}
