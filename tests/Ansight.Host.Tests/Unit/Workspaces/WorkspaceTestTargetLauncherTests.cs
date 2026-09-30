using System.IO.Compression;
using System.Security.Cryptography;
using Ansight.Host.Tests.TestSupport;
using Ansight.Host.Workspaces;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed partial class WorkspaceTestTargetLauncherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchAsync_ResolvesAndroidAvdNameToSerial(bool alreadyBooted)
    {
        var devices = new FakeHostDeviceService
        {
            StartedSerial = "emulator-5554",
            Inventory = new DeviceInventory([], [Device(
                alreadyBooted ? "emulator-5554" : "Demo_AVD", "Demo_AVD",
                DevicePlatforms.Android, isBooted: alreadyBooted)], [])
        };
        devices.SetApplications("emulator-5554", new InstalledApplication("com.example.target", "Target"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync("com.example.target",
            new WorkspaceTestTargetRequest(DeviceIdentifier: "Demo_AVD"), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("emulator-5554", devices.LaunchedDeviceIdentifier);
        Assert.Equal("emulator-5554", result.Target!.DeviceIdentifier);
        Assert.Equal(!alreadyBooted, result.Target.DeviceStarted);
    }

    [Fact]
    public async Task LaunchAsync_LaunchesInstalledApplicationOnOnlyMatchingBootedDevice()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [
                    Device("ios-1", "First iPhone", DevicePlatforms.Ios, isBooted: true),
                    Device("android-1", "Pixel", DevicePlatforms.Android, isBooted: true)
                ],
                [])
        };
        devices.SetApplications("ios-1", new InstalledApplication("com.example.target", "Target"));
        devices.SetApplications("android-1", new InstalledApplication("com.example.other", "Other"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            request: null,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ios-1", devices.LaunchedDeviceIdentifier);
        Assert.Equal("com.example.target", devices.LaunchedApplicationIdentifier);
        Assert.Empty(devices.StartedDeviceIdentifiers);
        Assert.Equal(["ios-1"], devices.ShownDeviceIdentifiers);
        Assert.False(result.Target!.DeviceStarted);
        Assert.False(result.Target.ApplicationInstalled);
    }

    [Fact]
    public async Task LaunchAsync_StartsExplicitShutdownDeviceWithoutBuildingApplication()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("ios-shutdown", "Stopped iPhone", DevicePlatforms.Ios, isBooted: false)],
                [])
        };
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(DeviceIdentifier: "ios-shutdown"),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["ios-shutdown"], devices.StartedDeviceIdentifiers);
        Assert.False(devices.StartOptions!.Headless);
        // StartAsync owns the window for a newly booted target; do not show it twice.
        Assert.Empty(devices.ShownDeviceIdentifiers);
        Assert.Equal("ios-shutdown", devices.LaunchedDeviceIdentifier);
        Assert.True(result.Target!.DeviceStarted);
        Assert.False(result.Target.ApplicationInstalled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LaunchAsync_HeadlessDoesNotShowWindow(bool isBooted, bool useCommandContext)
    {
        using var launchContext = DeviceLaunchContext.Push(useCommandContext);
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("ios-target", "iPhone", DevicePlatforms.Ios, isBooted)],
                [])
        };
        devices.SetApplications("ios-target", new InstalledApplication("com.example.target", "Target"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(
                DeviceIdentifier: "ios-target",
                Headless: !useCommandContext),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        if (isBooted)
        {
            Assert.Empty(devices.StartedDeviceIdentifiers);
        }
        else
        {
            Assert.Equal(["ios-target"], devices.StartedDeviceIdentifiers);
            Assert.True(devices.StartOptions!.Headless);
        }

        Assert.Empty(devices.ShownDeviceIdentifiers);
    }

    [Fact]
    public async Task LaunchAsync_StartsTheOnlyCompatibleShutdownDeviceAutomatically()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("ios-shutdown", "Stopped iPhone", DevicePlatforms.Ios, isBooted: false)],
                [])
        };
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(Platform: DevicePlatforms.Ios),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["ios-shutdown"], devices.StartedDeviceIdentifiers);
        Assert.Equal("ios-shutdown", devices.LaunchedDeviceIdentifier);
        Assert.True(result.Target!.DeviceStarted);
    }

    [Theory]
    [InlineData(DeviceKinds.Simulator, DevicePlatforms.Ios)]
    [InlineData(DeviceKinds.Emulator, DevicePlatforms.Android)]
    [InlineData(DeviceKinds.Avd, DevicePlatforms.Android)]
    [InlineData(DeviceKinds.Device, DevicePlatforms.Ios)]
    public async Task LaunchAsync_NormalizesDiscoveredDeviceKindAliases(
        string deviceKind,
        string platform)
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("target-1", "Target", platform, isBooted: true, kind: deviceKind)],
                [])
        };
        devices.SetApplications("target-1", new InstalledApplication("com.example.target", "Target"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(
                DeviceIdentifier: "target-1",
                DeviceKind: deviceKind),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("target-1", devices.LaunchedDeviceIdentifier);
    }

    [Fact]
    public async Task LaunchAsync_RequiresDeviceIdentifierWhenMultipleTargetsContainApplication()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [
                    Device("ios-1", "First iPhone", DevicePlatforms.Ios, isBooted: true),
                    Device("ios-2", "Second iPhone", DevicePlatforms.Ios, isBooted: true)
                ],
                [])
        };
        devices.SetApplications("ios-1", new InstalledApplication("com.example.target", "Target"));
        devices.SetApplications("ios-2", new InstalledApplication("com.example.target", "Target"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            request: null,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("--device-id", result.Message, StringComparison.Ordinal);
        Assert.Null(devices.LaunchedDeviceIdentifier);
    }

    [Fact]
    public async Task LaunchAsync_ExtractsIpaApplicationBundleForInstallation()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-launcher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            var ipaPath = Path.Combine(temporaryPath, "Target.ipa");
            using (var archive = ZipFile.Open(ipaPath, ZipArchiveMode.Create))
            {
                var executable = archive.CreateEntry("Payload/Target.app/Target");
                await using var writer = executable.Open();
                await writer.WriteAsync("simulator executable"u8.ToArray());
            }

            var devices = new FakeHostDeviceService
            {
                Inventory = new DeviceInventory(
                    [],
                    [Device(
                        "ios-1",
                        "iPhone",
                        DevicePlatforms.Ios,
                        isBooted: true,
                        kind: DeviceKinds.Device)],
                    [])
            };
            var launcher = new WorkspaceTestTargetLauncher(devices);

            var result = await launcher.LaunchAsync(
                "com.example.target",
                new WorkspaceTestTargetRequest(ApplicationPath: ipaPath),
                progress: null,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Message);
            Assert.True(devices.InstalledApplicationPathExisted);
            Assert.EndsWith("Target.app", devices.InstalledApplicationPath, StringComparison.Ordinal);
            Assert.False(Directory.Exists(devices.InstalledApplicationPath));
            Assert.True(result.Target!.ApplicationInstalled);
        }
        finally
        {
            Directory.Delete(temporaryPath, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchAsync_RejectsVirtualTargetForPhysicalIpa()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-launcher-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryPath);
        try
        {
            var ipaPath = Path.Combine(temporaryPath, "Target.ipa");
            using (var archive = ZipFile.Open(ipaPath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("Payload/Target.app/Target");
            }

            var devices = new FakeHostDeviceService
            {
                Inventory = new DeviceInventory(
                    [],
                    [Device("ios-1", "iPhone Simulator", DevicePlatforms.Ios, isBooted: true)],
                    [])
            };
            var launcher = new WorkspaceTestTargetLauncher(devices);

            var result = await launcher.LaunchAsync(
                "com.example.target",
                new WorkspaceTestTargetRequest(
                    ApplicationPath: ipaPath,
                    DeviceKind: DeviceKinds.Virtual),
                progress: null,
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Contains("requires a 'physical' target", result.Message, StringComparison.Ordinal);
            Assert.Null(devices.InstalledDeviceIdentifier);
            Assert.Null(devices.LaunchedDeviceIdentifier);
        }
        finally
        {
            Directory.Delete(temporaryPath, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchAsync_WhenInstalledApkChecksumMatches_SkipsInstallAndRestartsApplication()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-launcher-test-{Guid.NewGuid():N}.apk");
        var contents = "matching apk"u8.ToArray();
        await File.WriteAllBytesAsync(temporaryPath, contents);
        try
        {
            var devices = new FakeHostDeviceService
            {
                Inventory = new DeviceInventory(
                    [],
                    [Device(
                        "android-device-1",
                        "Pixel",
                        DevicePlatforms.Android,
                        isBooted: true,
                        kind: DeviceKinds.Device)],
                    [])
            };
            devices.SetApplicationChecksum(
                "android-device-1",
                "com.example.target",
                new ApplicationChecksum(Convert.ToHexStringLower(SHA256.HashData(contents))));
            var progress = new RecordingProgress<WorkspaceTestRunProgress>();
            var launcher = new WorkspaceTestTargetLauncher(devices);

            var result = await launcher.LaunchAsync(
                "com.example.target",
                new WorkspaceTestTargetRequest(
                    DeviceIdentifier: "android-device-1",
                    ApplicationPath: temporaryPath),
                progress,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Null(devices.InstalledApplicationPath);
            Assert.False(result.Target!.ApplicationInstalled);
            Assert.Equal(["terminate", "launch"], devices.Operations);
            Assert.Equal("com.example.target", devices.TerminatedApplicationIdentifier);
            Assert.Contains(progress.Values, value =>
                value.Stage == "app.reuse"
                && value.Message.Contains("checksum matches", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    [Fact]
    public async Task LaunchAsync_WhenInstalledApkChecksumDiffers_InstallsThenRestartsApplication()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ansight-launcher-test-{Guid.NewGuid():N}.apk");
        await File.WriteAllTextAsync(temporaryPath, "replacement apk");
        try
        {
            var devices = new FakeHostDeviceService
            {
                Inventory = new DeviceInventory(
                    [],
                    [Device(
                        "android-device-1",
                        "Pixel",
                        DevicePlatforms.Android,
                        isBooted: true,
                        kind: DeviceKinds.Device)],
                    [])
            };
            devices.SetApplicationChecksum(
                "android-device-1",
                "com.example.target",
                new ApplicationChecksum(new string('0', 64)));
            var launcher = new WorkspaceTestTargetLauncher(devices);

            var result = await launcher.LaunchAsync(
                "com.example.target",
                new WorkspaceTestTargetRequest(
                    DeviceIdentifier: "android-device-1",
                    ApplicationPath: temporaryPath),
                progress: null,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(temporaryPath, devices.InstalledApplicationPath);
            Assert.True(result.Target!.ApplicationInstalled);
            Assert.Equal(["install", "terminate", "launch"], devices.Operations);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    [Fact]
    public async Task LaunchAsync_WhenStopFails_DoesNotLaunchApplication()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("android-1", "Pixel", DevicePlatforms.Android, isBooted: true)],
                []),
            TerminateResult = DeviceOperationResult.Failure(
                "terminate-app",
                DevicePlatforms.Android,
                "android-1",
                "Stop failed.")
        };
        devices.SetApplications("android-1", new InstalledApplication("com.example.target", "Target"));
        var launcher = new WorkspaceTestTargetLauncher(devices);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            request: null,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("Stop failed.", result.Message);
        Assert.Equal(["terminate"], devices.Operations);
        Assert.Null(devices.LaunchedApplicationIdentifier);
    }

    [Fact]
    public async Task StopAsync_TerminatesApplicationOnLaunchedTarget()
    {
        var devices = new FakeHostDeviceService();
        var launcher = new WorkspaceTestTargetLauncher(devices);
        var target = new WorkspaceTestTarget(
            DevicePlatforms.Android,
            "android-1",
            "Pixel",
            "com.example.target",
            DeviceStarted: false,
            ApplicationInstalled: false,
            ApplicationLaunched: true);

        var result = await launcher.StopAsync(target, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["terminate"], devices.Operations);
        Assert.Equal("android-1", devices.TerminatedDeviceIdentifier);
        Assert.Equal("com.example.target", devices.TerminatedApplicationIdentifier);
    }

    [Fact]
    public async Task LaunchAsync_IssuesAndInjectsEnrollmentPayloadForPhysicalDevice()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device(
                    "android-device-1",
                    "Pixel",
                    DevicePlatforms.Android,
                    isBooted: true,
                    kind: DeviceKinds.Device)],
                [])
        };
        devices.SetApplications(
            "android-device-1",
            new InstalledApplication("com.example.target", "Target"));
        var enrollmentIssuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, enrollmentIssuer);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(DeviceIdentifier: "android-device-1"),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("com.example.target", enrollmentIssuer.IssuedAppId);
        Assert.Equal("invite-1", result.EnrollmentInviteId);
        Assert.Equal("ans2:one-use-payload", devices.LaunchOptions?.AnsightEnrollmentPayload);
        Assert.Null(enrollmentIssuer.RevokedInviteId);
    }

    [Fact]
    public async Task LaunchAsync_DoesNotIssueEnrollmentPayloadForVirtualDevice()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device("android-virtual-1", "Pixel Emulator", DevicePlatforms.Android, isBooted: true)],
                [])
        };
        devices.SetApplications(
            "android-virtual-1",
            new InstalledApplication("com.example.target", "Target"));
        var enrollmentIssuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, enrollmentIssuer);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            request: null,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(enrollmentIssuer.IssuedAppId);
        Assert.Null(result.EnrollmentInviteId);
        Assert.Null(devices.LaunchOptions);
    }

    [Fact]
    public async Task LaunchAsync_RevokesUnconsumedEnrollmentWhenPhysicalLaunchFails()
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory(
                [],
                [Device(
                    "ios-device-1",
                    "iPhone",
                    DevicePlatforms.Ios,
                    isBooted: true,
                    kind: DeviceKinds.Device)],
                []),
            LaunchResult = DeviceOperationResult.Failure(
                "launch-app",
                DevicePlatforms.Ios,
                "ios-device-1",
                "Launch failed.")
        };
        devices.SetApplications(
            "ios-device-1",
            new InstalledApplication("com.example.target", "Target"));
        var enrollmentIssuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, enrollmentIssuer);

        var result = await launcher.LaunchAsync(
            "com.example.target",
            new WorkspaceTestTargetRequest(DeviceIdentifier: "ios-device-1"),
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invite-1", enrollmentIssuer.RevokedInviteId);
        Assert.Equal("com.example.target", enrollmentIssuer.RevokedAppId);
    }

    private static DeviceDescriptor Device(
        string identifier,
        string name,
        string platform,
        bool isBooted,
        string? kind = null)
        => new(
            identifier,
            name,
            platform,
            "test-runtime",
            isBooted ? "booted" : "shutdown",
            isBooted,
            IsAvailable: true,
            kind ?? (platform == DevicePlatforms.Ios
                ? DeviceKinds.Simulator
                : DeviceKinds.Emulator));

    private sealed class FakeEnrollmentIssuer : IWorkspaceTestEnrollmentIssuer
    {
        public string? IssuedAppId { get; private set; }

        public string? RevokedInviteId { get; private set; }

        public string? RevokedAppId { get; private set; }

        public UnattendedEnrollmentIssueResult Issue(string appId)
        {
            IssuedAppId = appId;
            return UnattendedEnrollmentIssueResult.Success(
                "invite-1",
                "ans2:one-use-payload",
                DateTimeOffset.UtcNow.AddMinutes(10));
        }

        public void RevokeUnconsumed(string? inviteId, string appId)
        {
            RevokedInviteId = inviteId;
            RevokedAppId = appId;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }
}
