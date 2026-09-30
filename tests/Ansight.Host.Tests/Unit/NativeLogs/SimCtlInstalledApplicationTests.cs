using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.NativeLogs;

public sealed class SimCtlInstalledApplicationTests
{
    private const string DeviceUdid = "C868C8C0-F2DE-4C66-A337-142401E11B35";

    [Fact]
    public async Task GetInstalledApplicationsAsync_ReturnsUserAppsWithSearchableMetadata()
    {
        const string output = """
            {
                "com.alphaoutdoors.redpoint" =     {
                    ApplicationType = User;
                    CFBundleDisplayName = "Red-Point";
                    CFBundleIdentifier = "com.alphaoutdoors.redpoint";
                    CFBundleName = "Redpoint.Mobile";
                    CFBundleShortVersionString = "1.2.3";
                    CFBundleVersion = 456;
                    Bundle = "/simulator/Containers/Bundle/Application/ABC/Redpoint.app";
                    GroupContainers =         {
                    };
                };
                "com.apple.Preferences" =     {
                    ApplicationType = System;
                    CFBundleDisplayName = Settings;
                    CFBundleIdentifier = "com.apple.Preferences";
                    CFBundleName = Preferences;
                };
            }
            """;
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(0, output, string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var applications = await client.GetInstalledApplicationsAsync(DeviceUdid);

        var application = Assert.Single(applications);
        Assert.Equal("com.alphaoutdoors.redpoint", application.BundleIdentifier);
        Assert.Equal("Red-Point", application.DisplayName);
        Assert.Equal("Redpoint.Mobile", application.BundleName);
        Assert.Equal("1.2.3", application.Version);
        Assert.Equal("456", application.BuildVersion);
        Assert.Equal("/simulator/Containers/Bundle/Application/ABC/Redpoint.app", application.BundlePath);
        Assert.Equal(["simctl", "listapps", DeviceUdid], commandRunner.Arguments);
    }

    [Fact]
    public async Task GetDevicesAsync_ReturnsStoppedSimulatorDataPath()
    {
        const string dataPath = "/Users/test/Library/Developer/CoreSimulator/Devices/C868/data";
        var commandRunner = new RecordingCommandRunner(new SimCtlCommandResult(
            0,
            $$"""
              {
                "devices": {
                  "com.apple.CoreSimulator.SimRuntime.iOS-26-4": [
                    {
                      "dataPath": "{{dataPath}}",
                      "udid": "{{DeviceUdid}}",
                      "isAvailable": true,
                      "state": "Shutdown",
                      "name": "iPhone 17"
                    }
                  ]
                }
              }
              """,
            string.Empty));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var device = Assert.Single(await client.GetDevicesAsync());

        Assert.Equal(dataPath, device.DataPath);
        Assert.False(device.IsBooted);
    }

    [Fact]
    public async Task GetInstalledApplicationsAsync_ReportsSimCtlFailure()
    {
        var commandRunner = new RecordingCommandRunner(
            new SimCtlCommandResult(2, string.Empty, "Invalid device."));
        var client = new SimCtlClient(CreateResolution(), commandRunner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetInstalledApplicationsAsync(DeviceUdid));

        Assert.Contains("list installed simulator apps", exception.Message);
        Assert.Contains("Invalid device.", exception.Message);
    }

    private static SimCtlToolResolution CreateResolution()
        => SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/simctl",
            "test");

    private sealed class RecordingCommandRunner : ISimCtlCommandRunner
    {
        private readonly SimCtlCommandResult result;

        public RecordingCommandRunner(SimCtlCommandResult result)
        {
            this.result = result;
        }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<SimCtlCommandResult> RunAsync(
            SimCtlToolResolution toolResolution,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Arguments = arguments.ToArray();
            return Task.FromResult(result);
        }
    }
}
