namespace Ansight.Host.Runtime.Operations;

internal static class DeviceExecutionTools
{
    public static bool IsSupported(string toolName) => toolName is
        "ansight_take_screenshot" or "ansight_get_live_visual_tree" or
        "ansight_find_ui" or "ansight_scan_screen" or "ansight_wait_for_ui" or "ansight_assert_ui" or
        "ansight_tap_ui" or "ansight_type_text" or "ansight_swipe_ui" or "ansight_scroll_ui" or
        "ansight_pinch_ui" or "ansight_back_ui" or "ansight_run_ui_sequence" or
        "ansight_open_keyboard" or "ansight_dismiss_keyboard" or "ansight_is_keyboard_open" or
        "ansight_list_host_devices" or "ansight_start_device" or "ansight_launch_app" or
        "ansight_foreground_app" or "ansight_background_app" or "ansight_terminate_app" or
        "ansight_get_execution_capabilities" or "ansight_require_capabilities" or
        "ansight_list_sandbox_file" or "ansight_read_sandbox_file" or "ansight_capture_sandbox_file" or
        "ansight_list_tasks" or "ansight_run_task" or "ansight_describe_module" or
        "ansight_get_telemetry" or "ansight_get_telemetry_timeline" or "ansight_summarize_telemetry_window" or
        "ansight_get_logs" or "ansight_search_logs" or "ansight_get_log_context" or "ansight_get_log_facets" or
        "ansight_get_log_timeline" or "ansight_summarize_log_window" or "ansight_extract_exceptions" or
        "ansight_get_session_artifacts" or "ansight_get_nearest_artifacts" or "ansight_list_artifact_files" or
        "ansight_read_artifact_file" or "ansight_get_screenshot_frame" or "ansight_get_visual_tree_snapshot" or
        "ansight_search_visual_tree" or "ansight_get_session_properties" or "ansight_get_session_timeline" or
        "ansight_get_annotations" or "ansight_create_annotation" or "ansight_patch_annotation" or "ansight_remove_annotation" or
        "ansight_record_host_span" or
        "ansight_grant_permission" or "ansight_revoke_permission" or "ansight_query_permission" or
        "ansight_grant_ios_permission" or "ansight_revoke_ios_permission" or "ansight_query_ios_permission" or "ansight_reset_ios_permission" or
        "ansight_grant_android_permission" or "ansight_revoke_android_permission" or "ansight_query_android_permission";
}
