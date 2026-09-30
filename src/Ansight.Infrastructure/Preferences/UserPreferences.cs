using Ansight.Infrastructure.Theming;

namespace Ansight.Infrastructure.Preferences;

public sealed class UserPreferences : IUserPreferences
{
    private readonly IPreferencesStore preferencesStore;

    public UserPreferences(IPreferencesStore preferencesStore)
    {
        this.preferencesStore = preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        this.preferencesStore.Changed += OnPreferenceChanged;
    }

    public event EventHandler<UserPreferenceChangedEventArgs>? Changed;

    public string PreferredTheme
    {
        get => preferencesStore.Get(PreferenceKeys.PreferredTheme, ThemeIdentifiers.Dark);
        set => preferencesStore.Set(PreferenceKeys.PreferredTheme, value ?? string.Empty);
    }

    public string PreferredLanguage
    {
        get => preferencesStore.Get(PreferenceKeys.PreferredLanguage, "en");
        set => preferencesStore.Set(PreferenceKeys.PreferredLanguage, value ?? "en");
    }

    public bool UseDeviceLanguage
    {
        get => preferencesStore.Get(PreferenceKeys.UseDeviceLanguage, true);
        set => preferencesStore.Set(PreferenceKeys.UseDeviceLanguage, value);
    }

    public string LastOpenedRoute
    {
        get => preferencesStore.Get(PreferenceKeys.LastOpenedRoute, string.Empty);
        set => preferencesStore.Set(PreferenceKeys.LastOpenedRoute, value ?? string.Empty);
    }

    public string PreferredAiAgent
    {
        get => preferencesStore.Get(PreferenceKeys.PreferredAiAgent, "Codex");
        set => preferencesStore.Set(PreferenceKeys.PreferredAiAgent, value ?? "Codex");
    }

    public string PreferredBuildWorkspaceDirectory
    {
        get => preferencesStore.Get(PreferenceKeys.PreferredBuildWorkspaceDirectory, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.PreferredBuildWorkspaceDirectory,
            value?.Trim() ?? string.Empty);
    }

    public string LogCaptureLevel
    {
        get => preferencesStore.Get(PreferenceKeys.LogCaptureLevel, "Information");
        set => preferencesStore.Set(
            PreferenceKeys.LogCaptureLevel,
            string.IsNullOrWhiteSpace(value) ? "Information" : value);
    }

    public string AdbPath
    {
        get => preferencesStore.Get(PreferenceKeys.AdbPath, string.Empty);
        set => preferencesStore.Set(PreferenceKeys.AdbPath, value?.Trim() ?? string.Empty);
    }

    public string XcodePath
    {
        get => preferencesStore.Get(PreferenceKeys.XcodePath, string.Empty);
        set => preferencesStore.Set(PreferenceKeys.XcodePath, value?.Trim() ?? string.Empty);
    }

    public string RecentlyUsedSimulatorDeviceIdentifiers
    {
        get => preferencesStore.Get(PreferenceKeys.RecentlyUsedSimulatorDeviceIdentifiers, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.RecentlyUsedSimulatorDeviceIdentifiers,
            value ?? string.Empty);
    }

    public string PinnedSimulatorDeviceIdentifiers
    {
        get => preferencesStore.Get(PreferenceKeys.PinnedSimulatorDeviceIdentifiers, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.PinnedSimulatorDeviceIdentifiers,
            value ?? string.Empty);
    }

    public string ExternalSimulatorAccessMode
    {
        get => preferencesStore.Get(PreferenceKeys.ExternalSimulatorAccessMode, "disabled");
        set => preferencesStore.Set(
            PreferenceKeys.ExternalSimulatorAccessMode,
            string.IsNullOrWhiteSpace(value) ? "disabled" : value.Trim().ToLowerInvariant());
    }

    public string ExternalSimulatorAccessOwnerUserId
    {
        get => preferencesStore.Get(PreferenceKeys.ExternalSimulatorAccessOwnerUserId, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.ExternalSimulatorAccessOwnerUserId,
            value?.Trim() ?? string.Empty);
    }

    public string ExternalSimulatorMachineName
    {
        get => preferencesStore.Get(PreferenceKeys.ExternalSimulatorMachineName, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.ExternalSimulatorMachineName,
            value?.Trim() ?? string.Empty);
    }

    public string ExternalSimulatorTeamId
    {
        get => preferencesStore.Get(PreferenceKeys.ExternalSimulatorTeamId, string.Empty);
        set => preferencesStore.Set(
            PreferenceKeys.ExternalSimulatorTeamId,
            value?.Trim() ?? string.Empty);
    }

    public bool CaptureNativeSessionLogs
    {
        get => preferencesStore.Get(PreferenceKeys.CaptureNativeSessionLogs, true);
        set => preferencesStore.Set(PreferenceKeys.CaptureNativeSessionLogs, value);
    }

    public string UpdateReleaseChannel
    {
        get => preferencesStore.Get(PreferenceKeys.UpdateReleaseChannel, "public");
        set => preferencesStore.Set(
            PreferenceKeys.UpdateReleaseChannel,
            string.IsNullOrWhiteSpace(value) ? "public" : value);
    }

    public string TeamAppPublishingEnabledTeamIds
    {
        get => preferencesStore.Get(PreferenceKeys.TeamAppPublishingEnabledTeamIds, string.Empty);
        set => preferencesStore.Set(PreferenceKeys.TeamAppPublishingEnabledTeamIds, value ?? string.Empty);
    }

    public bool CaptureHostOperationLogs
    {
        get => preferencesStore.Get(PreferenceKeys.CaptureHostOperationLogs, true);
        set => preferencesStore.Set(PreferenceKeys.CaptureHostOperationLogs, value);
    }

    public bool CaptureFullHostOperationTrafficToDisk
    {
        get => preferencesStore.Get(PreferenceKeys.CaptureFullHostOperationTrafficToDisk, false);
        set => preferencesStore.Set(PreferenceKeys.CaptureFullHostOperationTrafficToDisk, value);
    }

    public bool SessionAutoCleanupEnabled
    {
        get => preferencesStore.Get(PreferenceKeys.SessionAutoCleanupEnabled, SessionCleanupPreferenceDefaults.Enabled);
        set => preferencesStore.Set(PreferenceKeys.SessionAutoCleanupEnabled, value);
    }

    public int SessionAutoCleanupRetentionDays
    {
        get => SessionCleanupPreferenceDefaults.NormalizeRetentionDays(
            preferencesStore.Get(
                PreferenceKeys.SessionAutoCleanupRetentionDays,
                SessionCleanupPreferenceDefaults.RetentionDays));
        set => preferencesStore.Set(
            PreferenceKeys.SessionAutoCleanupRetentionDays,
            SessionCleanupPreferenceDefaults.NormalizeRetentionDays(value));
    }

    public int SessionAutoCompactionAgeDays
    {
        get => SessionCleanupPreferenceDefaults.NormalizeCompactionAgeDays(
            preferencesStore.Get(
                PreferenceKeys.SessionAutoCompactionAgeDays,
                SessionCleanupPreferenceDefaults.CompactionAgeDays));
        set => preferencesStore.Set(
            PreferenceKeys.SessionAutoCompactionAgeDays,
            SessionCleanupPreferenceDefaults.NormalizeCompactionAgeDays(value));
    }

    public long SessionAutoCleanupMaximumCacheBytes
    {
        get => SessionCleanupPreferenceDefaults.NormalizeMaximumCacheBytes(
            preferencesStore.Get(
                PreferenceKeys.SessionAutoCleanupMaximumCacheBytes,
                SessionCleanupPreferenceDefaults.MaximumCacheBytes));
        set => preferencesStore.Set(
            PreferenceKeys.SessionAutoCleanupMaximumCacheBytes,
            SessionCleanupPreferenceDefaults.NormalizeMaximumCacheBytes(value));
    }

    public int MemorySpikeMinimumIncreasePercent
    {
        get => TelemetryAnalysisPreferenceDefaults.NormalizeMemorySpikeMinimumIncreasePercent(
            preferencesStore.Get(
                PreferenceKeys.MemorySpikeMinimumIncreasePercent,
                TelemetryAnalysisPreferenceDefaults.MemorySpikeMinimumIncreasePercent));
        set => preferencesStore.Set(
            PreferenceKeys.MemorySpikeMinimumIncreasePercent,
            TelemetryAnalysisPreferenceDefaults.NormalizeMemorySpikeMinimumIncreasePercent(value));
    }

    public int MemorySpikeMinimumIncreaseMegabytes
    {
        get => TelemetryAnalysisPreferenceDefaults.NormalizeMemorySpikeMinimumIncreaseMegabytes(
            preferencesStore.Get(
                PreferenceKeys.MemorySpikeMinimumIncreaseMegabytes,
                TelemetryAnalysisPreferenceDefaults.MemorySpikeMinimumIncreaseMegabytes));
        set => preferencesStore.Set(
            PreferenceKeys.MemorySpikeMinimumIncreaseMegabytes,
            TelemetryAnalysisPreferenceDefaults.NormalizeMemorySpikeMinimumIncreaseMegabytes(value));
    }

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        Changed?.Invoke(this, e);
    }
}
