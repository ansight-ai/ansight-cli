using System.Text;
using Ansight.Host.Runtime.DeviceExecution;
using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Runtime;

public sealed class DeviceAppWatchBackendTests
{
    [Fact]
    public void AutomaticDiscoveryUsesVirtualDevicesUnlessPhysicalDeviceIsExplicitlySelected()
    {
        var ios = new DeviceDescriptor("ios-udid", "iPhone", "ios", "test", "booted", true, true, DeviceKinds.Simulator);
        var android = new DeviceDescriptor("emulator-5554", "My_AVD", "android", "test", "booted", true, true, DeviceKinds.Emulator);
        var physical = ios with { Identifier = "physical", Kind = DeviceKinds.Physical };
        var physicalAndroid = android with { Identifier = "R58N123", Name = "Pixel", Kind = DeviceKinds.Device };
        var inventory = new[] { ios, android, physical, physicalAndroid, ios with { Identifier = "stopped", IsBooted = false },
            android with { Identifier = "offline", IsAvailable = false } };
        var watch = new AppWatchDefinition("watch", "test.app", null, null, true, []);
        Assert.Equal(new[] { ios, android }, DeviceAppWatchBackend.SelectDevices(watch, inventory));
        Assert.Equal(ios, Assert.Single(DeviceAppWatchBackend.SelectDevices(watch with { Platform = "ios" }, inventory)));
        Assert.Equal(android, Assert.Single(DeviceAppWatchBackend.SelectDevices(watch with { DeviceId = "My_AVD" }, inventory)));
        Assert.Equal(ios, Assert.Single(DeviceAppWatchBackend.SelectDevices(watch with { DeviceId = "ios-udid" }, inventory)));
        Assert.Empty(DeviceAppWatchBackend.SelectDevices(watch with { DeviceId = "physical" }, inventory));
        Assert.Equal(physical, Assert.Single(DeviceAppWatchBackend.SelectDevices(
            watch with { Platform = "ios", DeviceId = "physical" }, inventory)));
        Assert.Empty(DeviceAppWatchBackend.SelectDevices(watch with { DeviceId = "R58N123" }, inventory));
        Assert.Empty(DeviceAppWatchBackend.SelectDevices(
            watch with { Platform = "android", DeviceId = "Pixel" }, inventory));
        Assert.Equal(physicalAndroid, Assert.Single(DeviceAppWatchBackend.SelectDevices(
            watch with { Platform = "android", DeviceId = "R58N123" }, inventory)));
    }

    [Fact]
    public async Task AndroidDistinguishesConfirmedExitFromAdbFailureAndIdentifiesPidReuse()
    {
        using var environment = new TestEnvironment();
        var adb = Path.Combine(environment.RootPath, "adb");
        File.WriteAllText(adb, "mock executable");
        var commands = new Commands();
        var backend = new DeviceAppWatchBackend(null!, () => adb,
            (_, _, _, _) => throw new NotSupportedException(), commands);
        var watch = new AppWatchDefinition("watch", "test.app", null, null, true, []);
        var device = new DeviceDescriptor("emulator-5554", "Phone", "android", "test", "booted", true, true, DeviceKinds.Emulator);
        commands.Results.Enqueue(new DeviceCommandResult(1, [], ""));
        Assert.Null(await backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        commands.Results.Enqueue(new DeviceCommandResult(1, [], "device offline"));
        await Assert.ThrowsAsync<IOException>(() => backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        foreach (var birth in new[] { "100", "200" })
        {
            commands.Results.Enqueue(Result("23"));
            var fields = Enumerable.Repeat("0", 22).ToArray();
            fields[0] = "S";
            fields[19] = birth;
            commands.Results.Enqueue(Result("23 (test.app) " + string.Join(' ', fields)));
            Assert.Equal("23:" + birth, await backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        }
        Assert.All(commands.Arguments, arguments => Assert.Equal(device.Identifier, arguments[1]));
        Assert.Contains(commands.Arguments, arguments => arguments.Last() == "pidof 'test.app'");
    }

    [Fact]
    public async Task PhysicalAndroidObservesOnlyTheForegroundApp()
    {
        using var environment = new TestEnvironment();
        var adb = Path.Combine(environment.RootPath, "adb");
        File.WriteAllText(adb, "mock executable");
        var commands = new Commands();
        var backend = new DeviceAppWatchBackend(null!, () => adb,
            (_, _, _, _) => throw new NotSupportedException(), commands);
        var watch = new AppWatchDefinition("watch", "test.app", "android", "R58N123", true, []);
        var device = new DeviceDescriptor("R58N123", "Pixel", "android", "test", "device", true, true, DeviceKinds.Device);

        commands.Results.Enqueue(Result("topResumedActivity=ActivityRecord{123 other.app/.Main t1}"));
        Assert.Null(await backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        commands.Results.Enqueue(Result("topResumedActivity=ActivityRecord{123 test.app/.Main t1}"));
        commands.Results.Enqueue(Result("23"));
        var fields = Enumerable.Repeat("0", 22).ToArray();
        fields[0] = "S";
        fields[19] = "100";
        commands.Results.Enqueue(Result("23 (test.app) " + string.Join(' ', fields)));
        Assert.Equal("23:100", await backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        Assert.All(commands.Arguments, arguments => Assert.Equal(device.Identifier, arguments[1]));
        Assert.Equal(2, commands.Arguments.Count(arguments => arguments.Last() == "dumpsys activity activities"));
    }

    [Fact]
    public void PhysicalAndroidDoesNotGuessWhenForegroundStateIsMissing()
    {
        Assert.Throws<IOException>(() => AndroidForegroundActivity.IsForeground("No activity state", "test.app"));
        Assert.True(AndroidForegroundActivity.IsForeground(
            "mResumedActivity: ActivityRecord{123 test.app/.Main t1}", "test.app"));
    }

    [Theory]
    [InlineData("123\t0\tUIKitApplication:test.application[abc]")]
    [InlineData("-\t0\tUIKitApplication:test.app[abc]")]
    public async Task IosOnlyMatchesTheExactBundleAndRequiresARunningProcess(string launchctl)
    {
        var commands = new Commands();
        commands.Results.Enqueue(Result(launchctl));
        var backend = new DeviceAppWatchBackend(null!, () => null,
            (_, _, _, _) => throw new NotSupportedException(), commands);
        var watch = new AppWatchDefinition("watch", "test.app", "ios", "udid", true, []);
        var device = new DeviceDescriptor("udid", "Phone", "ios", "test", "booted", true, true, DeviceKinds.Simulator);
        Assert.Null(await backend.ReadIdentityAsync(watch, device, CancellationToken.None));
        Assert.Equal(new[] { "simctl", "spawn", "udid", "launchctl", "list" }, Assert.Single(commands.Arguments));
        commands.Results.Enqueue(new DeviceCommandResult(1, [], "Simulator service unavailable"));
        await Assert.ThrowsAsync<IOException>(() => backend.ReadIdentityAsync(watch, device, CancellationToken.None));
    }

    [Fact]
    public async Task PhysicalIosOnlyObservesAProcessWhenItsAppIsForeground()
    {
        var devices = new FakeHostDeviceService { PhysicalIosProcessIdentity = "17740" };
        devices.SetApplications("phone", new InstalledApplication("test.app", "Test", "/Applications/Test.app"));
        var foreground = false;
        var backend = new DeviceAppWatchBackend(devices, () => null,
            (_, _, _, _) => throw new NotSupportedException(),
            physicalIosForeground: (_, _, _) => Task.FromResult(foreground));
        var watch = new AppWatchDefinition("watch", "test.app", "ios", "phone", true, []);
        var device = new DeviceDescriptor("phone", "Phone", "ios", "test", "booted", true, true, DeviceKinds.Physical);

        var backgroundObservation = await backend.ReadObservationAsync(watch, device, CancellationToken.None);
        Assert.Null(backgroundObservation.ProcessIdentity);
        Assert.Equal(global::Ansight.AppLifecycleState.Background, backgroundObservation.AppState);
        Assert.NotNull(backgroundObservation.ObservedAtUtc);
        foreground = true;
        var foregroundObservation = await backend.ReadObservationAsync(watch, device, CancellationToken.None);
        Assert.Equal("17740", foregroundObservation.ProcessIdentity);
        Assert.Equal(global::Ansight.AppLifecycleState.Foreground, foregroundObservation.AppState);
        Assert.NotNull(foregroundObservation.ObservedAtUtc);
        devices.PhysicalIosProcessIdentity = null;
        var exitObservation = await backend.ReadObservationAsync(watch, device, CancellationToken.None);
        Assert.Null(exitObservation.ProcessIdentity);
        Assert.Equal(global::Ansight.AppLifecycleState.Unknown, exitObservation.AppState);
        foreground = false;
        var terminatedObservation = await backend.ReadObservationAsync(watch, device, CancellationToken.None);
        Assert.Null(terminatedObservation.ProcessIdentity);
        Assert.Equal(global::Ansight.AppLifecycleState.Background, terminatedObservation.AppState);
        Assert.NotNull(terminatedObservation.ObservedAtUtc);
        var inactiveObservation = await backend.ReadObservationAsync(watch, device, CancellationToken.None);
        Assert.Equal(global::Ansight.AppLifecycleState.Unknown, inactiveObservation.AppState);
    }

    private static DeviceCommandResult Result(string text) => new(0, Encoding.UTF8.GetBytes(text), "");

    private sealed class Commands : IDeviceCommandRunner
    {
        public Queue<DeviceCommandResult> Results { get; } = new();
        public List<IReadOnlyList<string>> Arguments { get; } = [];
        public Task<DeviceCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken, int maximumBytes = 1_048_576)
        {
            Arguments.Add(arguments);
            return Task.FromResult(Results.Dequeue());
        }
    }
}
