namespace Ansight.Host.Devices;

internal sealed record IosPhysicalDeviceAppiumOptions(
    string? ServerUrl,
    string? XcodeTeamId,
    string? XcodeSigningIdentity,
    string? WebDriverAgentBundleIdentifier,
    string? XcodeConfigurationFilePath)
{
    public static IosPhysicalDeviceAppiumOptions Resolve(RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new IosPhysicalDeviceAppiumOptions(
            FirstNonEmpty(
                options.IosPhysicalDeviceAppiumServerUrl,
                Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_SERVER_URL")),
            FirstNonEmpty(
                options.IosPhysicalDeviceXcodeTeamId,
                Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_XCODE_TEAM_ID")),
            FirstNonEmpty(
                options.IosPhysicalDeviceXcodeSigningIdentity,
                Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_XCODE_SIGNING_ID")),
            FirstNonEmpty(
                options.IosPhysicalDeviceWebDriverAgentBundleIdentifier,
                Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_WDA_BUNDLE_ID")),
            FirstNonEmpty(
                options.IosPhysicalDeviceXcodeConfigurationFilePath,
                Environment.GetEnvironmentVariable("ANSIGHT_APPIUM_XCODE_CONFIG_FILE")));
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
