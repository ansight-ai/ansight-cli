namespace Ansight.Infrastructure.Preferences;

public interface IUserPreferences
{
    event EventHandler<UserPreferenceChangedEventArgs>? Changed;

    string PreferredTheme { get; set; }

    string PreferredLanguage { get; set; }

    bool UseDeviceLanguage { get; set; }

    string LastOpenedRoute { get; set; }

    string PreferredAiAgent { get; set; }

    string PreferredBuildWorkspaceDirectory { get; set; }

    string LogCaptureLevel { get; set; }

    string AdbPath { get; set; }

    string XcodePath { get; set; }

    string RecentlyUsedSimulatorDeviceIdentifiers { get; set; }

    string PinnedSimulatorDeviceIdentifiers { get; set; }

    string ExternalSimulatorAccessMode { get; set; }

    string ExternalSimulatorAccessOwnerUserId { get; set; }

    string ExternalSimulatorMachineName { get; set; }

    string ExternalSimulatorTeamId { get; set; }

    bool CaptureNativeSessionLogs { get; set; }

    string UpdateReleaseChannel { get; set; }

    string TeamAppPublishingEnabledTeamIds { get; set; }

    bool CaptureHostOperationLogs { get; set; }

    bool CaptureFullHostOperationTrafficToDisk { get; set; }

    bool SessionAutoCleanupEnabled { get; set; }

    int SessionAutoCleanupRetentionDays { get; set; }

    int SessionAutoCompactionAgeDays { get; set; }

    long SessionAutoCleanupMaximumCacheBytes { get; set; }

    int MemorySpikeMinimumIncreasePercent { get; set; }

    int MemorySpikeMinimumIncreaseMegabytes { get; set; }
}
