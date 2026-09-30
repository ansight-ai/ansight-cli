using Ansight.Host.Tests.TestSupport;

namespace Ansight.Host.Tests.Unit.Workspaces;

public sealed partial class WorkspaceTestTargetLauncherTests
{
    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public async Task DeviceModeReusesOnlyTheMatchingMonitorWithoutRelaunching(string platform)
    {
        var device = Device("reuse-" + platform, "reuse-" + platform, platform, true);
        using var monitorClaim = DeviceExecutionClaim.Acquire(device);
        var devices = new FakeHostDeviceService { Inventory = new DeviceInventory([], [device], []) };
        devices.SetApplications(device.Identifier, new InstalledApplication("com.example.target", "Target"));
        var session = new AppSessionSnapshot
        {
            SessionId = "monitored", AppId = "com.example.target", CaptureSource = "device", ClientName = "Target",
            RemoteAddress = "host-device", ConfigId = null, CreatedUtc = DateTimeOffset.UtcNow,
            LastUpdatedUtc = DateTimeOffset.UtcNow, Status = "Capturing", IsHistorical = false,
            MetricChannels = [], Metrics = []
        };
        var launcher = new WorkspaceTestTargetLauncher(devices, findMonitoredSession: (selected, appId) =>
            selected.Identifier == device.Identifier && appId == session.AppId ? session : null);
        var request = new WorkspaceTestTargetRequest(DeviceIdentifier: device.Identifier) { ExecutionMode = "device" };

        var result = await launcher.LaunchAsync(session.AppId, request, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Same(session, result.ExistingSession);
        Assert.False(result.Target!.ApplicationLaunched);
        Assert.Null(result.DeviceClaim);
        Assert.Null(devices.LaunchedDeviceIdentifier);
        Assert.Empty(devices.ShownDeviceIdentifiers);
        Assert.Null(DeviceExecutionClaim.TryAcquire(device));

        var other = await launcher.LaunchAsync("com.example.other", request, null, CancellationToken.None);
        Assert.False(other.IsSuccess);
        Assert.Null(devices.LaunchedDeviceIdentifier);
        Assert.Null(DeviceExecutionClaim.TryAcquire(device));
    }

    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public async Task DeviceModeLaunchesWithoutEnrollmentAndReservesTarget(string platform)
    {
        var id = "device-mode-" + platform;
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory([], [Device(id, id, platform, true)], [])
        };
        devices.SetApplications(id, new InstalledApplication("com.example.target", "Target"));
        var issuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, issuer);
        var request = new WorkspaceTestTargetRequest(DeviceIdentifier: id) { ExecutionMode = "device" };
        var result = await launcher.LaunchAsync("com.example.target", request, null, CancellationToken.None);
        using (result.DeviceClaim)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("device", result.Target?.ExecutionMode);
            Assert.Null(issuer.IssuedAppId);
            var competing = await launcher.LaunchAsync("com.example.target", request, null, CancellationToken.None);
            Assert.False(competing.IsSuccess);
            Assert.Contains("active device execution", competing.Message);
        }
        var next = await launcher.LaunchAsync("com.example.target", request, null, CancellationToken.None);
        using (next.DeviceClaim) Assert.True(next.IsSuccess, next.Message);
    }

    [Theory]
    [InlineData("ios")]
    [InlineData("android")]
    public async Task DeviceModeRejectsPhysicalTargetBeforeMutatingDevice(string platform)
    {
        var devices = new FakeHostDeviceService
        {
            Inventory = new DeviceInventory([], [Device("physical", "Phone", platform, true, DeviceKinds.Physical)], [])
        };
        var launcher = new WorkspaceTestTargetLauncher(devices);
        var result = await launcher.LaunchAsync("com.example.target",
            new WorkspaceTestTargetRequest(DeviceIdentifier: "physical") { ExecutionMode = "device" },
            null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(devices.LaunchedDeviceIdentifier);
        Assert.Empty(devices.StartedDeviceIdentifiers);
        Assert.Empty(devices.ShownDeviceIdentifiers);
    }

    [Fact]
    public async Task DeviceModeReusesExplicitPhysicalAndroidMonitorWithoutEnrollment()
    {
        var device = Device("R58N123", "Pixel", DevicePlatforms.Android, true, DeviceKinds.Device);
        using var monitorClaim = DeviceExecutionClaim.Acquire(device);
        var devices = new FakeHostDeviceService { Inventory = new DeviceInventory([], [device], []) };
        devices.SetApplications(device.Identifier, new InstalledApplication("com.example.target", "Target"));
        var session = new AppSessionSnapshot
        {
            SessionId = "physical-android-monitor", AppId = "com.example.target", CaptureSource = "device",
            ClientName = "Target", RemoteAddress = "host-device", ConfigId = null,
            CreatedUtc = DateTimeOffset.UtcNow, LastUpdatedUtc = DateTimeOffset.UtcNow,
            Status = "Capturing", IsHistorical = false, MetricChannels = [], Metrics = []
        };
        var issuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, issuer,
            (selected, appId) => selected.Identifier == device.Identifier && appId == session.AppId ? session : null);
        var request = new WorkspaceTestTargetRequest(Platform: DevicePlatforms.Android,
            DeviceIdentifier: device.Identifier) { ExecutionMode = "device" };

        var result = await launcher.LaunchAsync(session.AppId, request, null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Same(session, result.ExistingSession);
        Assert.Null(result.DeviceClaim);
        Assert.Null(devices.LaunchedDeviceIdentifier);
        Assert.Null(issuer.IssuedAppId);
    }

    [Fact]
    public async Task DeviceModeLaunchesExplicitPhysicalAndroidWithoutEnrollment()
    {
        var device = Device("R58N124", "Pixel", DevicePlatforms.Android, true, DeviceKinds.Device);
        var devices = new FakeHostDeviceService { Inventory = new DeviceInventory([], [device], []) };
        devices.SetApplications(device.Identifier, new InstalledApplication("com.example.target", "Target"));
        var issuer = new FakeEnrollmentIssuer();
        var launcher = new WorkspaceTestTargetLauncher(devices, issuer);
        var request = new WorkspaceTestTargetRequest(Platform: DevicePlatforms.Android,
            DeviceIdentifier: device.Identifier) { ExecutionMode = "device" };

        var result = await launcher.LaunchAsync("com.example.target", request, null, CancellationToken.None);
        using (result.DeviceClaim)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(device.Identifier, devices.LaunchedDeviceIdentifier);
            Assert.Null(issuer.IssuedAppId);
            Assert.Equal(DeviceKinds.Device, result.Target?.DeviceKind);
        }
    }
}
