namespace Ansight.Host.UiAutomation;

/// <summary>
/// Canonical wire values used by visual-tree task contracts and persisted session evidence.
/// </summary>
public static class VisualTreeContract
{
    public const string AccessibilityKind = "accessibility";
    public const string DomKind = "dom";
    public const string FlutterKind = "flutter";
    public const string MauiKind = "maui";
    public const string NativeKind = "native";
    public const string ReactComponentKind = "react-component";
    public const string ReactShadowKind = "react-shadow";
    public const string UiKind = "ui";
    public const string UnknownKind = "unknown";

    public const string DeviceAccessibilityFormat = "ansight.device-accessibility.compact.v2";
    public const string DomFormat = "ansight.dom.visual-tree.compact.v2";
    public const string FlutterFormat = "ansight.flutter.visual-tree.compact.v2";
    public const string MauiFormat = "ansight.maui.visual-tree.compact.v2";
    public const string NativeFormat = "ansight.native.visual-tree.compact.v2";
    public const string ReactFormat = "ansight.react.visual-tree.compact.v2";
    public const string GenericFormat = "ansight.visual-tree.compact.v2";

    public const string AndroidRuntimePlatform = "android";
    public const string DotnetRuntimePlatform = "dotnet";
    public const string FlutterRuntimePlatform = "flutter";
    public const string IosRuntimePlatform = "ios";
    public const string LinuxRuntimePlatform = "linux";
    public const string MacCatalystRuntimePlatform = "maccatalyst";
    public const string MacOsRuntimePlatform = "macos";
    public const string UnknownRuntimePlatform = "unknown";
    public const string WebRuntimePlatform = "web";
    public const string WindowsRuntimePlatform = "windows";

    public const string DomToolId = "dom.get_document";
    public const string FlutterToolId = "flutter.get_widget_tree";
    public const string MauiToolId = "maui.get_visual_tree";
    public const string NativeToolId = "ui.get_visual_tree";
    public const string ReactComponentToolId = "react.get_component_tree";
    public const string ReactShadowToolId = "react.get_shadow_tree";

    public const string CurrentPageRootScope = "currentPage";
    public const string RootPageRootScope = "rootPage";
    public const string WindowRootScope = "window";
    public const string RootRootScope = "root";

    public static string NormalizeKind(
        string? toolId,
        string? reportedKind,
        string? source,
        string? format)
    {
        var normalizedToolId = Normalize(toolId);
        if (normalizedToolId is MauiToolId)
        {
            return MauiKind;
        }

        if (normalizedToolId is ReactComponentToolId)
        {
            return ReactComponentKind;
        }

        if (normalizedToolId is ReactShadowToolId)
        {
            return ReactShadowKind;
        }

        if (normalizedToolId is FlutterToolId)
        {
            return FlutterKind;
        }

        if (normalizedToolId is DomToolId)
        {
            return DomKind;
        }

        if (normalizedToolId is NativeToolId)
        {
            return NativeKind;
        }

        var normalizedKind = Normalize(reportedKind);
        if (normalizedKind is AccessibilityKind
            or MauiKind
            or ReactComponentKind
            or ReactShadowKind
            or FlutterKind
            or DomKind
            or NativeKind
            or UiKind)
        {
            return normalizedKind;
        }

        if (normalizedKind is "component")
        {
            return ReactComponentKind;
        }

        if (normalizedKind is "shadow")
        {
            return ReactShadowKind;
        }

        var normalizedSource = Normalize(source);
        if (normalizedSource is "device-accessibility" or "core-simulator-ax-service")
        {
            return AccessibilityKind;
        }

        if (normalizedSource is "react" or "react-native")
        {
            return normalizedKind is "shadow" ? ReactShadowKind : ReactComponentKind;
        }

        if (normalizedSource is MauiKind or FlutterKind or DomKind or NativeKind)
        {
            return normalizedSource;
        }

        return NormalizeFormat(format) switch
        {
            DeviceAccessibilityFormat => AccessibilityKind,
            MauiFormat => MauiKind,
            ReactFormat => normalizedKind is "shadow" ? ReactShadowKind : ReactComponentKind,
            FlutterFormat => FlutterKind,
            DomFormat => DomKind,
            NativeFormat => NativeKind,
            _ => UnknownKind
        };
    }

    public static string NormalizeFormat(string? format)
        => Normalize(format) switch
        {
            DeviceAccessibilityFormat => DeviceAccessibilityFormat,
            DomFormat => DomFormat,
            FlutterFormat => FlutterFormat,
            MauiFormat => MauiFormat,
            NativeFormat => NativeFormat,
            ReactFormat => ReactFormat,
            GenericFormat => GenericFormat,
            _ => GenericFormat
        };

    public static string NormalizeRuntimePlatform(string? platform)
        => Normalize(platform) switch
        {
            "android" or "android-native" => AndroidRuntimePlatform,
            "dotnet" or ".net" => DotnetRuntimePlatform,
            "flutter" => FlutterRuntimePlatform,
            "ios" or "ios-native" or "iphone" or "ipad" or "apple" => IosRuntimePlatform,
            "linux" => LinuxRuntimePlatform,
            "maccatalyst" or "mac-catalyst" => MacCatalystRuntimePlatform,
            "macos" or "macosx" or "osx" => MacOsRuntimePlatform,
            "web" or "web-dom" => WebRuntimePlatform,
            "windows" => WindowsRuntimePlatform,
            _ => UnknownRuntimePlatform
        };

    public static string NormalizeRootScope(string? rootScope, string? toolId = null)
        => NormalizeIdentifier(rootScope) switch
        {
            "currentpage" => CurrentPageRootScope,
            "rootpage" => RootPageRootScope,
            "window" => WindowRootScope,
            "root" => RootRootScope,
            _ when Normalize(toolId) is MauiToolId => CurrentPageRootScope,
            _ => RootRootScope
        };

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static string NormalizeIdentifier(string? value)
        => Normalize(value)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
}
