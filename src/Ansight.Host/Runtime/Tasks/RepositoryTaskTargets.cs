using Ansight.Host.Devices;

namespace Ansight.Host.Runtime.Tasks;

/// <summary>Canonical optional target metadata accepted by repository task descriptors.</summary>
public static class RepositoryTaskTargets
{
    public const string DotNetMaui = "dotnet-maui";
    public const string DotNetIos = "dotnet-ios";
    public const string DotNetAndroid = "dotnet-android";
    public const string ReactNative = "react-native";
    public const string Flutter = "flutter";
    public const string Capacitor = "capacitor";
    public const string IosUIKit = "ios-uikit";
    public const string IosSwiftUi = "ios-swiftui";
    public const string AndroidViews = "android-views";
    public const string AndroidCompose = "android-compose";
    public const string NativeUnknown = "native-unknown";

    public static IReadOnlyList<string> Platforms { get; } =
        [DevicePlatforms.Ios, DevicePlatforms.Android];

    public static IReadOnlyList<string> DeviceKinds { get; } =
        [Ansight.Host.Devices.DeviceKinds.Virtual, Ansight.Host.Devices.DeviceKinds.Physical];

    public static IReadOnlyList<string> Frameworks { get; } =
    [
        DotNetMaui,
        DotNetIos,
        DotNetAndroid,
        ReactNative,
        Flutter,
        Capacitor,
        IosUIKit,
        IosSwiftUi,
        AndroidViews,
        AndroidCompose,
        NativeUnknown
    ];

    public static bool FrameworkSupportsPlatform(string framework, string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return true;
        }

        return framework switch
        {
            DotNetIos or IosUIKit or IosSwiftUi => string.Equals(
                platform,
                DevicePlatforms.Ios,
                StringComparison.OrdinalIgnoreCase),
            DotNetAndroid or AndroidViews or AndroidCompose => string.Equals(
                platform,
                DevicePlatforms.Android,
                StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(platform, DevicePlatforms.Ios, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(platform, DevicePlatforms.Android, StringComparison.OrdinalIgnoreCase)
        };
    }

    public static string? ResolveRequiredPlatform(IReadOnlyList<string> frameworks)
    {
        if (frameworks.Count == 0)
        {
            return null;
        }

        var supportsIos = frameworks.Any(framework => FrameworkSupportsPlatform(framework, DevicePlatforms.Ios));
        var supportsAndroid = frameworks.Any(framework => FrameworkSupportsPlatform(framework, DevicePlatforms.Android));
        return (supportsIos, supportsAndroid) switch
        {
            (true, false) => DevicePlatforms.Ios,
            (false, true) => DevicePlatforms.Android,
            _ => null
        };
    }

    public static bool SupportsAnyFramework(
        IReadOnlyList<string> supportedFrameworks,
        IEnumerable<string> detectedFrameworks,
        string? platform = null)
        => supportedFrameworks.Count == 0
           || (supportedFrameworks.Contains(DotNetIos, StringComparer.OrdinalIgnoreCase)
               && string.Equals(platform, DevicePlatforms.Ios, StringComparison.OrdinalIgnoreCase))
           || (supportedFrameworks.Contains(DotNetAndroid, StringComparer.OrdinalIgnoreCase)
               && string.Equals(platform, DevicePlatforms.Android, StringComparison.OrdinalIgnoreCase))
           || detectedFrameworks
               .Select(NormalizeDetectedFramework)
               .Any(framework => supportedFrameworks.Contains(
                   framework,
                   StringComparer.OrdinalIgnoreCase));

    public static string NormalizeDetectedFramework(string framework)
        => string.Equals(framework, "maui", StringComparison.OrdinalIgnoreCase)
            ? DotNetMaui
            : framework.Trim().ToLowerInvariant();
}
