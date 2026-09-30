using System.Text.Json;
using Ansight.SimCtl;

namespace Ansight.Host.Tests.Unit.Devices;

public sealed class CoreDeviceClientTests
{
    [Fact]
    public void ParseRunningProcessMatchesInstalledBundlePathAndReportsAmbiguity()
    {
        using var document = JsonDocument.Parse(
            """
            {"result":{"runningProcesses":[
              {"processIdentifier":41,"executable":{"url":"file:////private/var/containers/Bundle/Application/ONE/Target.app/Target"}},
              {"processIdentifier":43,"executable":{"url":"file:////private/var/containers/Bundle/Application/ONE/Target.app/PlugIns/TargetWidgets.appex/TargetWidgets"}},
              {"processIdentifier":42,"executable":{"url":"file:///private/var/containers/Bundle/Application/TWO/Other.app/Other"}}
            ]}}
            """);
        Assert.Equal("41", CoreDeviceClient.ParseRunningApplicationProcessIdentity(document.RootElement,
            "com.example.target", "/private/var/containers/Bundle/Application/ONE/Target.app"));
        Assert.Equal("41", CoreDeviceClient.ParseRunningApplicationProcessIdentity(document.RootElement,
            "com.example.target", "file:///private/var/containers/Bundle/Application/ONE/Target.app"));
        Assert.Null(CoreDeviceClient.ParseRunningApplicationProcessIdentity(document.RootElement,
            "com.example.missing", "/private/var/containers/Bundle/Application/THREE/Missing.app"));
        using var ambiguous = JsonDocument.Parse(
            """
            {"result":{"runningProcesses":[
              {"processIdentifier":41,"executable":"file:///private/var/containers/Bundle/Application/ONE/Target.app/Target"},
              {"processIdentifier":44,"executable":"file:///private/var/containers/Bundle/Application/ONE/Target.app/TargetHelper"}
            ]}}
            """);
        Assert.Throws<IOException>(() => CoreDeviceClient.ParseRunningApplicationProcessIdentity(ambiguous.RootElement,
            "com.example.target", "/private/var/containers/Bundle/Application/ONE/Target.app"));
    }

    [Fact]
    public void ParseDevices_ReadsNestedCoreDeviceFieldsAndReadiness()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "result": {
                "devices": [
                  {
                    "identifier": "CORE-DEVICE-001",
                    "capabilities": [{ "name": "Acquire Usage Assertion" }],
                    "deviceProperties": {
                      "name": "Test iPhone",
                      "developerModeStatus": "enabled",
                      "osVersionNumber": "18.5"
                    },
                    "hardwareProperties": {
                      "platform": "iOS",
                      "productType": "iPhone17,1",
                      "udid": "00008140-0011223344556677"
                    },
                    "connectionProperties": {
                      "pairingState": "paired",
                      "tunnelState": "connected"
                    }
                  },
                  {
                    "identifier": "CORE-DEVICE-002",
                    "deviceProperties": {
                      "name": "Developer Mode Off",
                      "developerModeStatus": "disabled"
                    },
                    "hardwareProperties": {
                      "platform": "iOS",
                      "productType": "iPhone16,2"
                    },
                    "connectionProperties": {
                      "pairingState": "paired",
                      "tunnelState": "connected"
                    }
                  }
                ]
              }
            }
            """);

        var devices = CoreDeviceClient.ParseDevices(document.RootElement);

        Assert.Collection(
            devices,
            first =>
            {
                Assert.Equal("CORE-DEVICE-002", first.Identifier);
                Assert.Equal("developer-mode-disabled", first.State);
                Assert.False(first.IsAvailable);
            },
            second =>
            {
                Assert.Equal("00008140-0011223344556677", second.Identifier);
                Assert.Equal("Test iPhone", second.Name);
                Assert.Equal("iOS", second.Platform);
                Assert.Equal("iPhone17,1", second.ProductType);
                Assert.Equal("18.5", second.OperatingSystemVersion);
                Assert.Equal("connected", second.State);
                Assert.True(second.IsAvailable);
                Assert.True(second.IsIos);
            });
    }

    [Fact]
    public void ParseApplications_ReadsAndDeduplicatesBundleIdentifiers()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "result": {
                "apps": [
                  {
                    "bundleIdentifier": "com.example.zeta",
                    "name": "Zeta",
                    "version": "1.2.3",
                    "bundleVersion": "456",
                    "installedDate": "2026-08-25T01:02:03Z"
                  },
                  { "bundleID": "com.example.alpha", "displayName": "Alpha",
                    "installationURL": "file:///private/var/containers/Bundle/Application/ONE/Alpha.app" },
                  { "bundleIdentifier": "com.example.zeta", "name": "Duplicate" }
                ]
              }
            }
            """);

        var applications = CoreDeviceClient.ParseApplications(document.RootElement);

        Assert.Collection(
            applications,
            first =>
            {
                Assert.Equal("com.example.alpha", first.Identifier);
                Assert.Equal("Alpha", first.Name);
                Assert.Equal("file:///private/var/containers/Bundle/Application/ONE/Alpha.app", first.BundlePath);
            },
            second =>
            {
                Assert.Equal("com.example.zeta", second.Identifier);
                Assert.Equal("Zeta", second.Name);
                Assert.Equal("1.2.3", second.Version);
                Assert.Equal("456", second.BuildVersion);
                Assert.Equal(
                    new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero),
                    second.InstalledAtUtc);
            });
    }

    [Fact]
    public void CreateProcessStartInfo_UsesDeviceCtlChildEnvironmentWithoutArgumentExposure()
    {
        const string payload = "ans2:sensitive-one-use-payload";
        var resolution = SimCtlToolResolution.Found(
            "/Applications/Xcode.app/Contents/Developer",
            "/usr/bin/xcrun",
            "/usr/bin/xcrun",
            "test");

        var startInfo = CoreDeviceClient.CreateProcessStartInfo(
            resolution,
            ["device", "process", "launch", "--device", "device-1", "com.example.app"],
            "/tmp/devicectl-result.json",
            new Dictionary<string, string>
            {
                ["ANSIGHT_ENROLLMENT_PAYLOAD"] = payload
            });

        Assert.Equal(
            payload,
            startInfo.Environment["DEVICECTL_CHILD_ANSIGHT_ENROLLMENT_PAYLOAD"]);
        Assert.DoesNotContain(
            startInfo.ArgumentList,
            argument => argument?.Contains(payload, StringComparison.Ordinal) == true);
    }
}
