using System.Collections.Concurrent;
using System.Text.Json;
using Ansight.Host;

namespace Ansight.Cli.Tests.Commands.Device;

public sealed class DeviceCommandsTests
{
    private static readonly DeviceDescriptor[] devices =
    [
        Device("android-emulator", DevicePlatforms.Android, "emulator", "device", isBooted: true, DeviceFormFactors.Phone),
        Device("android-avd", DevicePlatforms.Android, "avd", "shutdown", isBooted: false, DeviceFormFactors.Phone),
        Device("android-phone", DevicePlatforms.Android, "device", "offline", isBooted: false, DeviceFormFactors.Phone),
        Device("ios-simulator", DevicePlatforms.Ios, "simulator", "Booted", isBooted: true, DeviceFormFactors.Phone),
        Device("ios-simulator-shutdown", DevicePlatforms.Ios, "simulator", "Shutdown", isBooted: false, DeviceFormFactors.Tablet),
        Device("ios-phone", DevicePlatforms.Ios, "physical", "connected", isBooted: true, DeviceFormFactors.Tablet)
    ];

    [Theory]
    [InlineData("device")]
    [InlineData("devices")]
    public async Task RunAsync_BareCommandListsDevicesAndSuggestsHelp(string command)
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await DeviceCommands.RunAsync(
            CliArguments.Parse([command]),
            new CliOutput(false, standardOutput, standardError),
            CancellationToken.None);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("only live devices are shown", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("--all", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains($"ansight {command} help", standardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_BareAllAliasEmitsOneJsonDocument()
    {
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await DeviceCommands.RunAsync(
            CliArguments.Parse(["devices", "--all", "--json"]),
            new CliOutput(true, standardOutput, standardError),
            CancellationToken.None);

        using var document = JsonDocument.Parse(standardOutput.ToString());
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            "ansight.devices/v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(string.Empty, standardError.ToString());
    }

    [Fact]
    public void FilterDevices_ShowsOnlyActiveDevicesByDefault()
    {
        var filtered = Filter("devices", "list");

        Assert.Equal(
            ["android-emulator", "ios-simulator", "ios-phone"],
            filtered.Select(static device => device.Identifier));
    }

    [Theory]
    [InlineData("--android", "android-emulator")]
    [InlineData("--ios", "ios-simulator", "ios-phone")]
    [InlineData("--emulator", "android-emulator")]
    [InlineData("--simulator", "ios-simulator")]
    [InlineData("--physical", "ios-phone")]
    public void FilterDevices_FiltersActiveDevicesByPlatformOrKind(
        string filter,
        params string[] expectedIdentifiers)
    {
        var filtered = Filter("devices", "list", filter);

        Assert.Equal(expectedIdentifiers, filtered.Select(static device => device.Identifier));
    }

    [Theory]
    [InlineData("--offline")]
    [InlineData("--all")]
    public void FilterDevices_AllAliasesIncludeInactiveDevices(string filter)
    {
        var filtered = Filter("devices", "list", filter);

        Assert.Equal(devices.Select(static device => device.Identifier),
            filtered.Select(static device => device.Identifier));
    }

    [Fact]
    public void FilterDevices_BareDevicesCommandAcceptsAllAlias()
    {
        var filtered = Filter("devices", "--all");

        Assert.Equal(devices.Select(static device => device.Identifier),
            filtered.Select(static device => device.Identifier));
    }

    [Theory]
    [InlineData("--emulator", "android-emulator", "android-avd")]
    [InlineData("--emulators", "android-emulator", "android-avd")]
    [InlineData("--simulator", "ios-simulator", "ios-simulator-shutdown")]
    [InlineData("--simulators", "ios-simulator", "ios-simulator-shutdown")]
    [InlineData("--physical-devices", "android-phone", "ios-phone")]
    public void FilterDevices_KindAliasesIncludeMatchingOfflineDevices(
        string filter,
        params string[] expectedIdentifiers)
    {
        var filtered = Filter("devices", "list", filter, "--offline");

        Assert.Equal(expectedIdentifiers, filtered.Select(static device => device.Identifier));
    }

    [Fact]
    public void FilterDevices_CombinesPlatformAndKindFilters()
    {
        var filtered = Filter("devices", "list", "--android", "--physical", "--offline");

        var device = Assert.Single(filtered);
        Assert.Equal("android-phone", device.Identifier);
    }

    [Theory]
    [InlineData("--phone", "android-emulator", "ios-simulator")]
    [InlineData("--phones", "android-emulator", "ios-simulator")]
    [InlineData("--tablet", "ios-phone")]
    [InlineData("--tablets", "ios-phone")]
    public void FilterDevices_FiltersLiveDevicesByFormFactor(
        string filter,
        params string[] expectedIdentifiers)
    {
        var filtered = Filter("devices", "list", filter);

        Assert.Equal(expectedIdentifiers, filtered.Select(static device => device.Identifier));
    }

    [Fact]
    public void FilterDevices_FormFactorComposesWithPlatformKindAndOfflineFilters()
    {
        var filtered = Filter("devices", "list", "--ios", "--simulator", "--tablet", "--all");

        var device = Assert.Single(filtered);
        Assert.Equal("ios-simulator-shutdown", device.Identifier);
    }

    [Fact]
    public void FilterDevices_CombinedFormFactorsIncludePhonesAndTablets()
    {
        var filtered = Filter("devices", "list", "--phone", "--tablet", "--all");

        Assert.Equal(
            devices.Select(static device => device.Identifier),
            filtered.Select(static device => device.Identifier));
    }

    [Fact]
    public async Task FilterDevicesByApplicationAsync_DefaultCandidatesStayLiveOnly()
    {
        var candidates = Filter("devices", "--app-id", "com.example.app");
        var inspectedIdentifiers = new ConcurrentBag<string>();

        var result = await DeviceCommands.FilterDevicesByApplicationAsync(
            candidates,
            "com.example.app",
            (device, _) =>
            {
                inspectedIdentifiers.Add(device.Identifier);
                return Task.FromResult<IReadOnlyList<InstalledApplication>>(
                    [new InstalledApplication("com.example.app", "Example")]);
            },
            CancellationToken.None);

        Assert.Equal(
            ["android-emulator", "ios-simulator", "ios-phone"],
            result.Devices.Select(static device => device.Identifier));
        Assert.Equal(
            ["android-emulator", "ios-phone", "ios-simulator"],
            inspectedIdentifiers.Order(StringComparer.Ordinal));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("--offline")]
    [InlineData("--all")]
    public async Task FilterDevicesByApplicationAsync_OfflineAliasesSearchInactiveCandidates(string scope)
    {
        var candidates = Filter("devices", "--app-id", "com.example.app", scope);
        var inspectedIdentifiers = new ConcurrentBag<string>();

        var result = await DeviceCommands.FilterDevicesByApplicationAsync(
            candidates,
            "COM.EXAMPLE.APP",
            (device, _) =>
            {
                inspectedIdentifiers.Add(device.Identifier);
                return Task.FromResult<IReadOnlyList<InstalledApplication>>(
                    device.Identifier == "ios-simulator-shutdown"
                        ? [new InstalledApplication("com.example.app", "Example")]
                        : []);
            },
            CancellationToken.None);

        var match = Assert.Single(result.Devices);
        Assert.Equal("ios-simulator-shutdown", match.Identifier);
        Assert.DoesNotContain("android-avd", inspectedIdentifiers);
        Assert.DoesNotContain("android-phone", inspectedIdentifiers);
        Assert.Equal(2, result.Failures.Count);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task FilterDevicesByApplicationAsync_ReportsTargetsThatCannotBeInspected()
    {
        var candidates = Filter("devices", "--all");

        var result = await DeviceCommands.FilterDevicesByApplicationAsync(
            candidates,
            "com.example.app",
            (device, _) => device.IsBooted
                ? Task.FromResult<IReadOnlyList<InstalledApplication>>([])
                : Task.FromException<IReadOnlyList<InstalledApplication>>(
                    new InvalidOperationException("Target is offline.")),
            CancellationToken.None);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("3 devices", warning, StringComparison.Ordinal);
        Assert.Contains("Results may be incomplete", warning, StringComparison.Ordinal);
        Assert.Contains("--verbose", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Target is offline", warning, StringComparison.Ordinal);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(
            result.Failures,
            static failure => failure.DeviceIdentifier == "ios-simulator-shutdown"
                              && failure.Message == "Target is offline.");
    }

    [Fact]
    public async Task FilterDevicesByApplicationAsync_FiltersAndReturnsVersionMetadata()
    {
        var candidates = Filter("devices", "--app-id", "com.example.app");

        var result = await DeviceCommands.FilterDevicesByApplicationAsync(
            candidates,
            "com.example.app",
            (device, _) => Task.FromResult<IReadOnlyList<InstalledApplication>>(
            [
                new InstalledApplication(
                    "com.example.app",
                    "Example",
                    Version: device.Identifier == "ios-simulator" ? "2.0" : "1.0",
                    BuildVersion: device.Identifier == "ios-simulator" ? "42" : "10")
            ]),
            CancellationToken.None,
            applicationVersion: "2.0",
            applicationBuildVersion: "42");

        var device = Assert.Single(result.Devices);
        Assert.Equal("ios-simulator", device.Identifier);
        var match = Assert.Single(result.Matches);
        Assert.Equal("2.0", match.Application.Version);
        Assert.Equal("42", match.Application.BuildVersion);
    }

    [Theory]
    [InlineData("2.10.*", "42?", "ios-simulator")]
    [InlineData("1.0", "10", "android-emulator", "ios-phone")]
    [InlineData("2.?0.?", "421", "ios-simulator")]
    public async Task FilterDevicesByApplicationAsync_SupportsExactAndWildcardMetadataFilters(
        string versionPattern,
        string buildPattern,
        params string[] expectedDeviceIdentifiers)
    {
        var candidates = Filter("devices", "--app-id", "com.example.app");

        var result = await DeviceCommands.FilterDevicesByApplicationAsync(
            candidates,
            "com.example.app",
            (device, _) => Task.FromResult<IReadOnlyList<InstalledApplication>>(
            [
                new InstalledApplication(
                    "com.example.app",
                    "Example",
                    Version: device.Identifier == "ios-simulator" ? "2.10.3" : "1.0",
                    BuildVersion: device.Identifier == "ios-simulator" ? "421" : "10")
            ]),
            CancellationToken.None,
            applicationVersion: versionPattern,
            applicationBuildVersion: buildPattern);

        Assert.Equal(expectedDeviceIdentifiers, result.Devices.Select(static device => device.Identifier));
    }

    [Fact]
    public void ResolveApplicationVersionFilter_AcceptsVersionAlias()
    {
        var arguments = CliArguments.Parse(
            ["devices", "--app-id", "com.example.app", "--version", "2.10.*"]);

        var versionPattern = DeviceCommands.ResolveApplicationVersionFilter(
            arguments,
            "com.example.app");

        Assert.Equal("2.10.*", versionPattern);
    }

    [Fact]
    public void ResolveApplicationVersionFilter_RejectsBothAliasNames()
    {
        var arguments = CliArguments.Parse(
            [
                "devices",
                "--app-id", "com.example.app",
                "--version", "2.10.*",
                "--app-version", "2.10.*"
            ]);

        var exception = Assert.Throws<CliUsageException>(() =>
            DeviceCommands.ResolveApplicationVersionFilter(arguments, "com.example.app"));

        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderInventory_AlignsEveryDeviceColumn()
    {
        var inventory = new DeviceInventory(
            [],
            [
                new DeviceDescriptor(
                    "2A201FDH200BXX",
                    "Pixel 7",
                    DevicePlatforms.Android,
                    string.Empty,
                    "device",
                    IsBooted: true,
                    IsAvailable: true,
                    "device"),
                new DeviceDescriptor(
                    "E84FF6CC-835C-4BE4-904C-541357C05F6C",
                    "Codex Work App iPad A 20260604-100118",
                    DevicePlatforms.Ios,
                    string.Empty,
                    "Booted",
                    IsBooted: true,
                    IsAvailable: true,
                    "simulator")
            ],
            []);

        var rendered = DeviceCommands.RenderInventory(
            inventory,
            includesOfflineDevices: true,
            applicationIdentifier: null,
            []);
        var lines = rendered.Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.Equal(lines[0].IndexOf("device", StringComparison.Ordinal),
            lines[1].IndexOf("simulator", StringComparison.Ordinal));
        Assert.Equal(lines[0].IndexOf("2A201FDH200BXX", StringComparison.Ordinal),
            lines[1].IndexOf("E84FF6CC-835C-4BE4-904C-541357C05F6C", StringComparison.Ordinal));
        Assert.Equal(lines[0].LastIndexOf("device", StringComparison.Ordinal),
            lines[1].IndexOf("Booted", StringComparison.Ordinal));
        Assert.Equal(lines[0].IndexOf("Pixel 7", StringComparison.Ordinal),
            lines[1].IndexOf("Codex Work App", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderInventory_AppMatchIncludesVersionBuildAndInstallationTimes()
    {
        var device = devices[0];
        var installedAtUtc = new DateTimeOffset(2026, 8, 25, 1, 2, 3, TimeSpan.Zero);
        var updatedAtUtc = new DateTimeOffset(2026, 8, 26, 4, 5, 6, TimeSpan.Zero);
        var inventory = new DeviceInventory([], [device], []);
        var application = new InstalledApplication(
            "com.example.app",
            "Example",
            Version: "1.2.3",
            BuildVersion: "456",
            InstalledAtUtc: installedAtUtc,
            LastUpdatedAtUtc: updatedAtUtc);

        var rendered = DeviceCommands.RenderInventory(
            inventory,
            includesOfflineDevices: true,
            "com.example.app",
            [new DeviceApplicationMatchOutput(device.Identifier, application)]);

        Assert.Contains("version=1.2.3", rendered, StringComparison.Ordinal);
        Assert.Contains("build=456", rendered, StringComparison.Ordinal);
        Assert.Contains($"installed={installedAtUtc:O}", rendered, StringComparison.Ordinal);
        Assert.Contains($"updated={updatedAtUtc:O}", rendered, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DeviceDescriptor> Filter(params string[] arguments)
        => DeviceCommands.FilterDevices(devices, CliArguments.Parse(arguments));

    private static DeviceDescriptor Device(
        string identifier,
        string platform,
        string kind,
        string state,
        bool isBooted,
        string? formFactor = null)
        => new(
            identifier,
            identifier,
            platform,
            string.Empty,
            state,
            isBooted,
            isBooted,
            kind)
        {
            FormFactor = formFactor
        };
}
