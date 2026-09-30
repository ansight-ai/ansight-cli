namespace Ansight.Infrastructure.Preferences;

public static class PreferenceKeys
{
    // Persisted identifiers remain stable so installed CLIs and rollbacks share settings.
    public const string PreferredTheme = "ansight.studio.preferences.theme";
    public const string PreferredLanguage = "ansight.studio.preferences.language";
    public const string UseDeviceLanguage = "ansight.studio.preferences.use_device_language";
    public const string LastOpenedRoute = "ansight.studio.preferences.last_opened_route";
    public const string PreferredAiAgent = "ansight.studio.preferences.preferred_ai_agent";
    public const string PreferredBuildWorkspaceDirectory = "ansight.studio.preferences.preferred_build_workspace_directory";
    public const string LastDeviceLocation = "ansight.studio.preferences.last_device_location";
    public const string LogCaptureLevel = "ansight.studio.preferences.log_capture_level";
    public const string AdbPath = "ansight.studio.preferences.adb_path";
    public const string XcodePath = "ansight.studio.preferences.xcode_path";
    public const string RecentlyUsedSimulatorDeviceIdentifiers = "ansight.studio.preferences.recently_used_simulator_device_identifiers";
    public const string PinnedSimulatorDeviceIdentifiers = "ansight.studio.preferences.pinned_simulator_device_identifiers";
    public const string ExternalSimulatorAccessMode = "ansight.studio.preferences.external_simulator_access_mode";
    public const string ExternalSimulatorAccessOwnerUserId = "ansight.studio.preferences.external_simulator_access_owner_user_id";
    public const string ExternalSimulatorMachineName = "ansight.studio.preferences.external_simulator_machine_name";
    public const string ExternalSimulatorTeamId = "ansight.studio.preferences.external_simulator_team_id";
    public const string CaptureNativeSessionLogs = "ansight.studio.preferences.capture_native_session_logs";
    public const string UpdateReleaseChannel = "ansight.studio.preferences.update_release_channel";
    public const string TeamAppPublishingEnabledTeamIds = "ansight.studio.preferences.team_app_publishing_enabled_team_ids";
    public const string CaptureHostOperationLogs = "ansight.studio.preferences.capture_host_operation_logs";
    public const string CaptureFullHostOperationTrafficToDisk = "ansight.studio.preferences.capture_full_host_operation_traffic_to_disk";
    public const string SessionAutoCleanupEnabled = "ansight.studio.preferences.session_auto_cleanup_enabled";
    public const string SessionAutoCleanupRetentionDays = "ansight.studio.preferences.session_auto_cleanup_retention_days";
    public const string SessionAutoCompactionAgeDays = "ansight.studio.preferences.session_auto_compaction_age_days";
    public const string SessionAutoCleanupMaximumCacheBytes = "ansight.studio.preferences.session_auto_cleanup_maximum_cache_bytes";
    public const string MemorySpikeMinimumIncreasePercent = "ansight.studio.preferences.memory_spike_minimum_increase_percent";
    public const string MemorySpikeMinimumIncreaseMegabytes = "ansight.studio.preferences.memory_spike_minimum_increase_megabytes";
    public const string LastLaunchedVersion = "ansight.studio.lifecycle.last_launched_version";
    public const string SdkMigrationNoticeAcknowledgedVersion = "ansight.studio.lifecycle.sdk_migration_notice_acknowledged_version";
}
