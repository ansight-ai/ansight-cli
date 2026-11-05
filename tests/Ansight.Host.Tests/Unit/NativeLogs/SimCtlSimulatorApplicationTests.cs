using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class SimCtlSimulatorApplicationTests
{
    private const string DeviceUdid = "AC42C25B-229C-4EAC-B7F7-3BC888988022";

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PreserveSimulatorLaunchWhenPresentOrNoReplacementExists(bool simulatorExists, bool hubExists)
    {
        var developerDirectory = Path.GetFullPath(Path.Combine("test-xcodes", "Xcode 26.app", "Contents", "Developer"));
        var simulatorPath = Path.Combine(developerDirectory, "Applications", "Simulator.app");
        var request = SimCtlClient.CreateSimulatorOpenRequest(
            developerDirectory,
            DeviceUdid,
            path => path == simulatorPath ? simulatorExists : hubExists);

        Assert.Equal("/usr/bin/open", request.ExecutablePath);
        Assert.Equal(developerDirectory, request.DeveloperDirectory);
        Assert.Equal(
            ["-a", simulatorPath, "--args", "-CurrentDeviceUDID", DeviceUdid],
            request.Arguments);
    }

    [Fact]
    public void OpenExactDeviceInDeviceHubFromTheSelectedXcode()
    {
        var developerDirectory = Path.GetFullPath(Path.Combine("test-xcodes", "Xcode 27.app", "Contents", "Developer"));
        var hubPath = Path.GetFullPath(Path.Combine(developerDirectory, "..", "Applications", "DeviceHub.app"));
        var request = SimCtlClient.CreateSimulatorOpenRequest(
            developerDirectory,
            DeviceUdid,
            path => path == hubPath);

        Assert.Equal("/usr/bin/open", request.ExecutablePath);
        Assert.Equal(developerDirectory, request.DeveloperDirectory);
        Assert.Equal(["-a", hubPath, $"devices://device/open?id={DeviceUdid}"], request.Arguments);
    }

    [Fact]
    public void DeviceHubIdentifierCannotAddAnotherQueryParameter()
    {
        var developerDirectory = Path.GetFullPath(Path.Combine("test-xcodes", "Xcode 27.app", "Contents", "Developer"));
        var request = SimCtlClient.CreateSimulatorOpenRequest(
            developerDirectory,
            "device&another=value",
            path => Path.GetFileName(path) == "DeviceHub.app");

        Assert.Equal("devices://device/open?id=device%26another%3Dvalue", request.Arguments.Last());
    }
}
