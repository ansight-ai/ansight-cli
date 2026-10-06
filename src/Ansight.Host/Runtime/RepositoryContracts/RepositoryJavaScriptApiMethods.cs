namespace Ansight.Host.Runtime.RepositoryContracts;

internal static class RepositoryJavaScriptApiMethods
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> standardHostToolSuites =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["spans"] = Suite(new StandardApiMethod("record", "ansight_record_host_span")),
            ["capabilities"] = Suite(
                new StandardApiMethod("snapshot", "ansight_get_execution_capabilities"),
                new StandardApiMethod("require", "ansight_require_capabilities")),
            ["files"] = Suite(
                new StandardApiMethod("list", "ansight_list_sandbox_file"),
                new StandardApiMethod("read", "ansight_read_sandbox_file"),
                new StandardApiMethod("capture", "ansight_capture_sandbox_file")),
            ["annotations"] = Suite(
                new StandardApiMethod("get", "ansight_get_annotations"),
                new StandardApiMethod("create", "ansight_create_annotation"),
                new StandardApiMethod("update", "ansight_patch_annotation"),
                new StandardApiMethod("delete", "ansight_remove_annotation")),
            ["appTools"] = Suite(
                new StandardApiMethod("list", "ansight_list_app_tools")),
            ["artifacts"] = Suite(
                new StandardApiMethod("getNearest", "ansight_get_nearest_artifacts"),
                new StandardApiMethod("getSession", "ansight_get_session_artifacts"),
                new StandardApiMethod("listFiles", "ansight_list_artifact_files"),
                new StandardApiMethod("readFile", "ansight_read_artifact_file")),
            ["database"] = Suite(
                new StandardApiMethod("assert", "ansight_assert_database")),
            ["permissions"] = Suite(
                new StandardApiMethod("grant", "ansight_grant_permission"),
                new StandardApiMethod("revoke", "ansight_revoke_permission"),
                new StandardApiMethod("query", "ansight_query_permission")),
            ["permissions.ios"] = Suite(
                new StandardApiMethod("grant", "ansight_grant_ios_permission"),
                new StandardApiMethod("revoke", "ansight_revoke_ios_permission"),
                new StandardApiMethod("query", "ansight_query_ios_permission"),
                new StandardApiMethod("reset", "ansight_reset_ios_permission")),
            ["permissions.android"] = Suite(
                new StandardApiMethod("grant", "ansight_grant_android_permission"),
                new StandardApiMethod("revoke", "ansight_revoke_android_permission"),
                new StandardApiMethod("query", "ansight_query_android_permission")),
            ["device"] = Suite(
                new StandardApiMethod("audioCapabilities", "ansight_get_audio_capabilities"),
                new StandardApiMethod("injectMicrophoneAudio", "ansight_inject_audio"),
                new StandardApiMethod("playLocation", "ansight_play_device_location"),
                new StandardApiMethod("setLocation", "ansight_set_device_location"),
                new StandardApiMethod("clearLocation", "ansight_clear_device_location"),
                new StandardApiMethod("shake", "ansight_shake_device"),
                new StandardApiMethod("playAccelerometer", "ansight_play_accelerometer")),
            ["lifecycle"] = Suite(
                new StandardApiMethod("launch", "ansight_launch_app"),
                new StandardApiMethod("foreground", "ansight_foreground_app"),
                new StandardApiMethod("background", "ansight_background_app"),
                new StandardApiMethod("terminate", "ansight_terminate_app")),
            ["keyboard"] = Suite(
                new StandardApiMethod("open", "ansight_open_keyboard"),
                new StandardApiMethod("isOpen", "ansight_is_keyboard_open"),
                new StandardApiMethod("dismiss", "ansight_dismiss_keyboard")),
            ["logs"] = Suite(
                new StandardApiMethod("get", "ansight_get_logs"),
                new StandardApiMethod("search", "ansight_search_logs"),
                new StandardApiMethod("getContext", "ansight_get_log_context"),
                new StandardApiMethod("getFacets", "ansight_get_log_facets"),
                new StandardApiMethod("getTimeline", "ansight_get_log_timeline"),
                new StandardApiMethod("summarizeWindow", "ansight_summarize_log_window"),
                new StandardApiMethod("extractExceptions", "ansight_extract_exceptions")),
            ["network"] = Suite(
                new StandardApiMethod("get", "ansight_get_network_requests"),
                new StandardApiMethod("getRequest", "ansight_get_network_request"),
                new StandardApiMethod("readBody", "ansight_read_network_body")),
            ["screenshots"] = Suite(
                new StandardApiMethod("take", "ansight_take_screenshot"),
                new StandardApiMethod("getFrame", "ansight_get_screenshot_frame"),
                new StandardApiMethod("assert", "ansight_assert_screenshot")),
            ["session"] = Suite(
                new StandardApiMethod("getAppState", "ansight_get_app_state"),
                new StandardApiMethod("getProperties", "ansight_get_session_properties"),
                new StandardApiMethod("getTimeline", "ansight_get_session_timeline")),
            ["telemetry"] = Suite(
                new StandardApiMethod("get", "ansight_get_telemetry"),
                new StandardApiMethod("getTimeline", "ansight_get_telemetry_timeline"),
                new StandardApiMethod("summarizeWindow", "ansight_summarize_telemetry_window")),
            ["touches"] = Suite(
                new StandardApiMethod("getTimeline", "ansight_get_touch_timeline"),
                new StandardApiMethod("getContext", "ansight_get_touch_context"),
                new StandardApiMethod("getGestureSegments", "ansight_get_gesture_segments"),
                new StandardApiMethod("findTapTargets", "ansight_find_tap_targets"),
                new StandardApiMethod("getHeatmap", "ansight_get_touch_heatmap"),
                new StandardApiMethod("findDead", "ansight_find_dead_touches"),
                new StandardApiMethod("getArtifacts", "ansight_get_touch_artifacts"),
                new StandardApiMethod("summarizeFlow", "ansight_summarize_touch_flow")),
            ["ui"] = Suite(
                new StandardApiMethod("find", "ansight_find_ui"),
                new StandardApiMethod("scanScreen", "ansight_scan_screen"),
                new StandardApiMethod("waitFor", "ansight_wait_for_ui"),
                new StandardApiMethod("assert", "ansight_assert_ui"),
                new StandardApiMethod("tap", "ansight_tap_ui"),
                new StandardApiMethod("typeText", "ansight_type_text"),
                new StandardApiMethod("swipe", "ansight_swipe_ui"),
                new StandardApiMethod("scroll", "ansight_scroll_ui"),
                new StandardApiMethod("pinch", "ansight_pinch_ui"),
                new StandardApiMethod("back", "ansight_back_ui"),
                new StandardApiMethod("runSequence", "ansight_run_ui_sequence"),
                new StandardApiMethod("getLiveVisualTree", "ansight_get_live_visual_tree"),
                new StandardApiMethod("getLiveNavigationStructure", "ansight_get_live_navigation_structure"),
                new StandardApiMethod("getVisualTreeSnapshot", "ansight_get_visual_tree_snapshot"),
                new StandardApiMethod("searchVisualTree", "ansight_search_visual_tree"))
        };
    private static readonly IReadOnlyDictionary<string, RepositoryHostApiMethod> standardHostMethodsByToolName =
        standardHostToolSuites
            .SelectMany(suite => suite.Value.Select(method => new RepositoryHostApiMethod(
                suite.Key,
                method.Key,
                method.Value)))
            .ToDictionary(method => method.ToolName, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> taskCompositionSuites =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["tasks"] = Suite(
                new StandardApiMethod("list", "list"),
                new StandardApiMethod("run", "run"))
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> taskApiSuites =
        standardHostToolSuites
            .Where(suite => suite.Key != "spans")
            .Append(new KeyValuePair<string, IReadOnlyDictionary<string, string>>("spans",
                Suite(new StandardApiMethod("measure", "ansight_record_host_span"))))
            .Concat(taskCompositionSuites)
            .ToDictionary(
                suite => suite.Key,
                suite => suite.Value,
                StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> standardAppToolSuites =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["artifacts"] = Suite(
                new StandardApiMethod("query", "artifacts.query"),
                new StandardApiMethod("request", "artifacts.request")),
            ["ui"] = Suite(
                new StandardApiMethod("getVisualTree", "ui.get_visual_tree"),
                new StandardApiMethod("getScreenshot", "ui.get_screenshot"),
                new StandardApiMethod("inspectNode", "ui.inspect_node"),
                new StandardApiMethod("showOverlay", "ui.show_overlay"),
                new StandardApiMethod("getOverlay", "ui.get_overlay"),
                new StandardApiMethod("queryOverlays", "ui.query_overlays"),
                new StandardApiMethod("updateOverlay", "ui.update_overlay"),
                new StandardApiMethod("removeOverlay", "ui.remove_overlay"),
                new StandardApiMethod("clearOverlays", "ui.clear_overlays")),
            ["files"] = Suite(
                new StandardApiMethod("listDirectory", "files.list_directory"),
                new StandardApiMethod("readFile", "files.read_file"),
                new StandardApiMethod("getChecksum", "files.get_file_checksum"),
                new StandardApiMethod("download", "files.download_file"),
                new StandardApiMethod("beginBinaryDownload", "files.begin_binary_download"),
                new StandardApiMethod("push", "files.push_file"),
                new StandardApiMethod("copy", "files.copy_file"),
                new StandardApiMethod("move", "files.move_file"),
                new StandardApiMethod("delete", "files.delete_file")),
            ["fileDescriptors"] = Suite(
                new StandardApiMethod("listOpen", "file_descriptors.list_open"),
                new StandardApiMethod("countOpen", "file_descriptors.count_open"),
                new StandardApiMethod("inspect", "file_descriptors.inspect"),
                new StandardApiMethod("getUsage", "file_descriptors.get_usage")),
            ["jniReferences"] = Suite(
                new StandardApiMethod("captureGraph", "jni_references.capture_graph")),
            ["clipboard"] = Suite(
                new StandardApiMethod("getText", "clipboard.get_text"),
                new StandardApiMethod("hasText", "clipboard.has_text"),
                new StandardApiMethod("setText", "clipboard.set_text"),
                new StandardApiMethod("clear", "clipboard.clear")),
            ["preferences"] = Suite(
                new StandardApiMethod("listKeys", "prefs.list_keys"),
                new StandardApiMethod("get", "prefs.get_value"),
                new StandardApiMethod("set", "prefs.set_value"),
                new StandardApiMethod("remove", "prefs.remove_key")),
            ["secureStorage"] = Suite(
                new StandardApiMethod("get", "secure.get_value"),
                new StandardApiMethod("set", "secure.set_value"),
                new StandardApiMethod("remove", "secure.remove_key")),
            ["data"] = Suite(
                new StandardApiMethod("listDatabases", "data.list_databases"),
                new StandardApiMethod("describeSchema", "data.describe_schema"),
                new StandardApiMethod("query", "data.query")),
            ["reflection"] = Suite(
                new StandardApiMethod("listRoots", "reflect.list_roots"),
                new StandardApiMethod("inspectObject", "reflect.inspect_object"),
                new StandardApiMethod("describeType", "reflect.describe_type"),
                new StandardApiMethod("setMemberValue", "reflect.set_member_value"),
                new StandardApiMethod("invokeMethod", "reflect.invoke_method")),
            ["maui"] = Suite(
                new StandardApiMethod("getCurrentPage", "maui.get_current_page"),
                new StandardApiMethod("getVisualTree", "maui.get_visual_tree"),
                new StandardApiMethod("findElements", "maui.find_elements"),
                new StandardApiMethod("getElement", "maui.get_element"),
                new StandardApiMethod("getBindableProperty", "maui.get_bindable_property"),
                new StandardApiMethod("setBindableProperty", "maui.set_bindable_property"),
                new StandardApiMethod("clearBindableProperty", "maui.clear_bindable_property"),
                new StandardApiMethod("inflateXaml", "maui.inflate_xaml"),
                new StandardApiMethod("addElement", "maui.add_element"),
                new StandardApiMethod("removeElement", "maui.remove_element"),
                new StandardApiMethod("setAppTheme", "maui.set_app_theme"),
                new StandardApiMethod("getBindingContext", "maui.get_binding_context"),
                new StandardApiMethod("getBindings", "maui.get_bindings"),
                new StandardApiMethod("getResourceState", "maui.get_resource_state"),
                new StandardApiMethod("getNavigationState", "maui.get_navigation_state"),
                new StandardApiMethod("invokeElementAction", "maui.invoke_element_action"),
                new StandardApiMethod("waitForUi", "maui.wait_for_ui"),
                new StandardApiMethod("getLayoutDiagnostics", "maui.get_layout_diagnostics"),
                new StandardApiMethod("getHandlerDiagnostics", "maui.get_handler_diagnostics"),
                new StandardApiMethod("invokeBindingContextCommand", "maui.invoke_binding_context_command"),
                new StandardApiMethod("setBindingContextProperty", "maui.set_binding_context_property")),
            ["react"] = Suite(
                new StandardApiMethod("getComponentTree", "react.get_component_tree"),
                new StandardApiMethod("getShadowTree", "react.get_shadow_tree"),
                new StandardApiMethod("findComponents", "react.find_components"),
                new StandardApiMethod("getComponent", "react.get_component"),
                new StandardApiMethod("getNavigationState", "react.get_navigation_state"),
                new StandardApiMethod("invokeComponentAction", "react.invoke_component_action")),
            ["flutter"] = Suite(
                new StandardApiMethod("getWidgetTree", "flutter.get_widget_tree"),
                new StandardApiMethod("inspectWidget", "flutter.inspect_widget"),
                new StandardApiMethod("findWidgets", "flutter.find_widgets"),
                new StandardApiMethod("getNavigationState", "flutter.get_navigation_state")),
            ["capacitor"] = Suite(
                new StandardApiMethod("getDocument", "dom.get_document"),
                new StandardApiMethod("inspectNode", "dom.inspect_node"),
                new StandardApiMethod("querySelector", "dom.query_selector"),
                new StandardApiMethod("invokeAction", "dom.invoke_action"))
        };
    private static readonly IReadOnlySet<string> standardAppToolIds = standardAppToolSuites.Values
        .SelectMany(static suite => suite.Values)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StandardAppToolSuites
        => standardAppToolSuites;

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StandardHostToolSuites
        => standardHostToolSuites;

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TaskCompositionSuites
        => taskCompositionSuites;

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TaskApiSuites
        => taskApiSuites;

    public static RepositoryHostApiMethod ResolveHostApiMethod(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return standardHostMethodsByToolName.TryGetValue(toolName, out var method)
            ? method
            : throw new InvalidOperationException(
                $"Registered host tool '{toolName}' does not have a repository task API feature mapping.");
    }

    public static bool IsStandardAppToolId(string toolId)
        => standardAppToolIds.Contains(toolId);

    private static IReadOnlyDictionary<string, string> Suite(params StandardApiMethod[] methods)
        => methods.ToDictionary(method => method.MethodName, method => method.ToolId, StringComparer.Ordinal);

    private readonly record struct StandardApiMethod(string MethodName, string ToolId);
}

internal sealed record RepositoryHostApiMethod(
    string FeatureName,
    string MethodName,
    string ToolName);
