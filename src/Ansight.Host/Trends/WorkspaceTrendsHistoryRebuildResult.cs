namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryRebuildResult(
    string DatabasePath,
    bool DryRun,
    string? AppId,
    string? AppVersion,
    string? MetricKey,
    int RemovedHistoryResultCount,
    int RebuiltHistoryResultCount,
    IReadOnlyList<WorkspaceTrendsHistoryRebuildAppResult> Apps,
    IReadOnlyList<WorkspaceTrendsHistoryRebuildSkippedApp> SkippedApps);
