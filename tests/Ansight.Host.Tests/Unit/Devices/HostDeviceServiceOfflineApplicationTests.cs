namespace Ansight.Host.Tests.Unit.Devices;

public sealed class HostDeviceServiceOfflineApplicationTests
{
    [Fact]
    public async Task ReadInstalledSimulatorApplicationsAsync_ReadsAppsFromShutdownSimulatorData()
    {
        var simulatorDataPath = Path.Combine(
            Path.GetTempPath(),
            $"ansight-offline-simulator-apps-{Guid.NewGuid():N}");
        try
        {
            var applicationPath = Path.Combine(
                simulatorDataPath,
                "Containers",
                "Bundle",
                "Application",
                Guid.NewGuid().ToString("D"),
                "Example.app");
            Directory.CreateDirectory(applicationPath);
            await File.WriteAllTextAsync(
                Path.Combine(applicationPath, "Info.plist"),
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <plist version="1.0">
                  <dict>
                    <key>CFBundleIdentifier</key>
                    <string>com.example.app</string>
                    <key>CFBundleDisplayName</key>
                    <string>Example App</string>
                    <key>CFBundleName</key>
                    <string>Example</string>
                    <key>CFBundleShortVersionString</key>
                    <string>1.2.3</string>
                    <key>CFBundleVersion</key>
                    <string>456</string>
                  </dict>
                </plist>
                """);

            var applications = await DeviceService.ReadInstalledSimulatorApplicationsAsync(
                simulatorDataPath);

            var application = Assert.Single(applications);
            Assert.Equal("com.example.app", application.Identifier);
            Assert.Equal("Example App", application.Name);
            Assert.Equal("1.2.3", application.Version);
            Assert.Equal("456", application.BuildVersion);
            Assert.NotNull(application.InstalledAtUtc);
        }
        finally
        {
            if (Directory.Exists(simulatorDataPath))
            {
                Directory.Delete(simulatorDataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadInstalledSimulatorApplicationsAsync_MissingApplicationDataReturnsEmpty()
    {
        var applications = await DeviceService.ReadInstalledSimulatorApplicationsAsync(
            Path.Combine(Path.GetTempPath(), $"ansight-missing-simulator-{Guid.NewGuid():N}"));

        Assert.Empty(applications);
    }
}
