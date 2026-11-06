namespace Ansight.Host.Replay;

public sealed record SessionExplorerBootstrap(
    string Schema,
    string Mode,
    string? InitialSessionId,
    bool SupportsLiveSessions,
    bool SupportsDeviceLocation,
    bool SupportsRouteReplay,
    bool SupportsTrends,
    bool SupportsTestHistory,
    bool SupportsRegisteredApps,
    bool SupportsEnrollmentInvites,
    bool SupportsSettings,
    bool SupportsAppGraphProgress,
    bool SupportsAppGraphRecording,
    bool SupportsCloudSessions,
    bool SupportsDeviceManagement,
    bool SupportsSessionAdministration,
    bool SupportsAccountManagement,
    bool SupportsHostHealth,
    bool SupportsTestExecution,
    bool SupportsTaskExtraction,
    string? MapboxAccessToken)
{
    public IReadOnlyDictionary<string, string> ExtensionUi { get; init; } = new Dictionary<string, string>();
}
