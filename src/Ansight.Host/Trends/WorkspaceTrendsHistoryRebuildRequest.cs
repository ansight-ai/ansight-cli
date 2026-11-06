namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsHistoryRebuildRequest(
    string? AppId = null,
    string? AppVersion = null,
    string? MetricKey = null,
    string? WorkspacePath = null,
    bool DryRun = false);
