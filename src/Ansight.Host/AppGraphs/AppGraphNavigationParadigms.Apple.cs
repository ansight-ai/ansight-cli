namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm UIKit(string kind) => kind switch
    {
        "ui_navigation_controller" => Stack("UINavigationController.viewControllers ordered root to top.", "viewControllers and topViewController; correlate visibleViewController with any presentation.", true),
        "ui_tab_bar_controller" => Selection("UITabBarController.viewControllers in reported order.", "viewControllers, selectedViewController and selectedIndex.", "Switching tabs does not pop their UINavigationControllers; preserve each tab's stack."),
        "ui_split_view_controller" => Split("UISplitViewController columns and their nested controllers.", "Column roles, display mode, collapsed state and visible controller containment."),
        "presented_view_controller" => Presentation("One observed presentation relationship; preserve any navigation controller inside the presented content.", "presentingViewController, presentedViewController, presentation style and foreground window scene."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing UIKit navigation paradigm.")
    };

    private static AppGraphNavigationParadigm SwiftUI(string kind) => kind switch
    {
        "navigation_stack" => Stack("NavigationStack root plus app-exposed path and any observed view-based destinations; retain each stack's identity.", "Exposed NavigationPath/selection and visible destinations; private hosting implementation types are not route evidence."),
        "navigation_split_view" => Split("NavigationSplitView sidebar/content/detail columns and any nested NavigationStacks.", "App-exposed column visibility, selection and navigation paths; collapsed presentation may show one column."),
        "tab_view" => Selection("TabView's declared tabs and selection tags in reported order.", "App-exposed TabView selection and tab membership; preserve each tab's independent path.", "Tab selection and NavigationStack path changes are distinct; observe app-defined return behavior."),
        "sheet" or "full_screen_cover" => Presentation("Observed sheet or fullScreenCover content, including any nested NavigationStack.", "App-exposed presentation binding/item, presenter and foreground content."),
        "window_group" => new("window_collection", "WindowGroup scene instances, each with independent navigation and presentation state.", "Record focused scene without assuming other windows are invisible.", "Closing a window is not a navigation pop and may be unsafe during discovery.", "App-exposed scene identity plus native window/focus evidence."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing SwiftUI navigation paradigm.")
    };

    private static AppGraphNavigationParadigm AppKit(string kind) => kind switch
    {
        "ns_page_controller" => Selection("NSPageController arrangedObjects identify selectable pages; they are not automatically a push/pop stack.", "arrangedObjects, selectedIndex and displayed view controller.", "Record page transitions and any application-defined history; do not infer UINavigationController semantics."),
        "ns_split_view_controller" => Split("NSSplitViewController splitViewItems and nested content controllers.", "splitViewItems, collapse state and focused pane."),
        "ns_tab_view_controller" => Selection("NSTabViewController tabViewItems in reported order.", "tabViewItems and selectedTabViewItemIndex.", "Tab selection preserves each child's navigation; Back is app-defined."),
        "sheet" or "popover" => Presentation("Observed sheet/popover content and its owner; nested content keeps its own hierarchy.", "NSWindow sheet ownership or NSPopover presentation and visible view-controller containment."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing AppKit navigation paradigm.")
    };
}
