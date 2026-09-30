<!-- template:maui -->
Treat Shell, FlyoutItem, TabBar, and other persistent navigation roots as hosts. Inventory every host child from rootPage or window structure, and model NavigationPage and modal stacks as routes. For an internal tab set, record the currently selected tab before using any tab action.
<!-- /template -->

<!-- template:react-native -->
Use React Navigation drawer, tab, and stack router state as the authoritative host hierarchy. Preserve nested route state, route keys, and each router's active index. Record the active route of every tab router before changing tabs.
<!-- /template -->

<!-- template:flutter -->
Use Navigator and nested Navigator stacks as route structure. Treat BottomNavigationBar, NavigationRail, TabBar, DefaultTabController, and TabController membership as ordered host or tab groups, including the initially selected child.
<!-- /template -->

<!-- template:capacitor -->
Use router outlets, URL/history state, tab bars, elements with tablist/tab/tabpanel roles, and persistent DOM navigation containers. Record the selected tab panel before invoking sibling tab elements.
<!-- /template -->

<!-- template:ios-uikit -->
Use foreground UIWindowScene and UIViewController containment as the canonical hierarchy. Model UINavigationController stacks, presented view controllers, UISplitViewController columns, and custom drawer containers as routes or hosts. Treat UITabBarController.viewControllers as an ordered host and record selectedIndex before selecting another tab.
<!-- /template -->

<!-- template:ios-swiftui -->
Use app-exposed NavigationStack paths, NavigationSplitView columns, sheets, full-screen covers, and TabView selection as navigation state. Record the initial TabView selection before changing it. Treat UIHostingController boundaries as correlation evidence only; do not invent hidden SwiftUI destinations from private implementation types or a flattened accessibility tree.
<!-- /template -->

<!-- template:maccatalyst-uikit -->
Use UIKit scene and view-controller containment on Mac Catalyst. Model UINavigationController, UISplitViewController, presented controllers, and UITabBarController membership as canonical structure, recording the selected tab before changing it.
<!-- /template -->

<!-- template:maccatalyst-swiftui -->
Use app-exposed SwiftUI NavigationStack, NavigationSplitView, sheet, window, and TabView state on Mac Catalyst. Record initial selection before actions and correlate SwiftUI destinations with their UIKit hosting boundaries without treating those hosts as separate screens.
<!-- /template -->

<!-- template:macos-appkit -->
Use NSWindow and NSViewController containment as the canonical hierarchy. Model NSPageController, NSSplitViewController, sheets, popovers, and NSTabViewController membership explicitly, recording selectedTabViewItemIndex before changing tabs.
<!-- /template -->

<!-- template:macos-swiftui -->
Use app-exposed SwiftUI NavigationStack, NavigationSplitView, WindowGroup, sheet, and TabView state on macOS. Record the initial TabView selection and use NSHostingController or NSHostingView only to correlate SwiftUI content with its AppKit host.
<!-- /template -->

<!-- template:android-views -->
Use the foreground Activity, FragmentManager/NavController state, and Android View containment. Treat DrawerLayout, BottomNavigationView, NavigationRailView, and other persistent roots as hosts. Model TabLayout with ViewPager or ViewPager2 as an ordered tab group and record the selected item or page before changing it.
<!-- /template -->

<!-- template:android-compose -->
Use app-exposed NavHost/NavController routes and back stacks as authoritative structure. Treat ModalNavigationDrawer, NavigationBar, NavigationRail, TabRow, PrimaryTabRow, SecondaryTabRow, and pager state as hosts or tab groups. Record selectedItemIndex or currentPage before actions, and do not turn recomposition or transient semantics-node replacement into destinations.
<!-- /template -->

<!-- template:native-unknown -->
The native UI toolkit is not identified. Use only observed platform-neutral containment, selected state, tab/drawer roles, and stable actions. Do not infer a UI toolkit from the implementation language or runtime.
<!-- /template -->
