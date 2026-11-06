namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    public static AppGraphNavigationParadigm Resolve(string framework, string kind) => framework switch
    {
        "maui" => Maui(kind),
        "react-native" => ReactNative(kind),
        "flutter" => Flutter(kind),
        "capacitor" => Web(kind),
        "ios-uikit" or "maccatalyst-uikit" => UIKit(kind),
        "ios-swiftui" or "maccatalyst-swiftui" or "macos-swiftui" => SwiftUI(kind),
        "macos-appkit" => AppKit(kind),
        "android-views" => AndroidViews(kind),
        "android-compose" => AndroidCompose(kind),
        _ => new AppGraphNavigationParadigm(
            "observed_containment", "Only directly observed destinations; do not infer hidden children.",
            "Record observed selection or focus without assuming exclusive visibility.",
            "Determine return behavior from observed actions; toolkit is unknown.",
            "Native accessibility and visible containment; no toolkit inferred from language.")
    };

    private static AppGraphNavigationParadigm Stack(string inventory, string evidence, bool enforceTop = false) => new(
        "ordered_stack", inventory,
        "Preserve root-to-top order and identify the active route; keep nested stacks separate.",
        "Pop within this stack; at its root, observe parent handling rather than inventing a return. Record caller context.",
        evidence, enforceTop);

    private static AppGraphNavigationParadigm Selection(string inventory, string evidence, string returnBehavior) => new(
        "branch_selection", inventory,
        "Record the selected child before switching; preserve each child's nested navigation state.",
        returnBehavior, evidence);

    private static AppGraphNavigationParadigm Presentation(string inventory, string evidence) => new(
        "modal_presentation", inventory,
        "Record the presented content and its presenter; nested navigation belongs to a separate child host.",
        "Dismiss returns to the observed presenter, while an inner stack may pop without dismissing the presentation.",
        evidence);

    private static AppGraphNavigationParadigm Split(string inventory, string evidence) => new(
        "adaptive_columns", inventory,
        "Several columns may be visible together. activeChildDestinationId denotes focus, not exclusive visibility; retain collapsed-column state.",
        "Observe column selection and nested-stack returns separately; a layout collapse is not a navigation pop.",
        evidence);
}
