<!-- template:maui -->
For this confirmed MAUI app, prefer device accessibility and the published native visual tree for visible controls,
sheets, dialogs, and the active window. When ui.get_visual_tree is published, prefer it for visible app content.
MAUI currentPage, rootPage, and window trees describe the logical framework hierarchy and may omit custom sheets
hosted in separate native windows. Use them for bindings, framework state, and navigation structure. An unchanged
logical page tree must not override native evidence of a visible sheet. Scope targets to the foreground sheet;
do not interact with its background page. A truncated tree is incomplete evidence: use a focused query or increase
its bounded depth before declaring a control missing.
Treat Shell, FlyoutPage, FlyoutItem, TabbedPage, TabBar, and other persistent navigation roots as hosts. Preserve FlyoutPage.Detail and Shell containment: a flyout owns the tabbed-page host, which owns its tab screens. Record direct children only; a flyout with one tabbed-page child is complete when the structural inventory confirms it. Do not flatten its tabs into peer flyout destinations. Inventory every host child from rootPage or window structure, and model NavigationPage and modal stacks as routes. For an internal tab set, record the currently selected tab before using any tab action.
<!-- /template -->

<!-- template:avalonia -->
Use the published Avalonia visual tree for rendered controls and template children, the logical tree for framework
containment, and the accessibility tree for automation-peer names, roles, values, and supported actions. Native
accessibility may expose only the Avalonia host; that does not establish that the framework controls are absent.
Inspect attached top levels and popup roots when a dialog or menu appears, and scope interaction to the visible
foreground content. A truncated tree is incomplete evidence; query a bounded subtree using its observed stable ID.
Record observed tab selection and drawer state before changing them. Preserve nested containment and use only
exposed state for navigation structure; do not infer routes from control or view-model class names. For user journeys,
use real device input on observed controls and verify the resulting visible state. Property mutation and remote
method invocation do not prove that the corresponding touch, keyboard, focus, or accessibility interaction works.
<!-- /template -->

<!-- template:react-native -->
When exposed by the app, use React Navigation drawer, tab, and stack router state as the authoritative host hierarchy. Preserve nested route state, route keys, and each router's active index. Record the active route of every tab router before changing tabs. Do not infer unexposed router state from component names.
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
