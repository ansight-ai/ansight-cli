namespace Ansight.Host.Trends;

public sealed record WorkspaceTrendsReport(
    int SchemaVersion,
    WorkspaceTrendsRunContext Context,
    WorkspaceTrendsStatus Status,
    string Message,
    IReadOnlyList<WorkspaceTrendsCheckResult> Checks,
    IReadOnlyList<WorkspaceTrendsHistoryDecision> History,
    string? SidecarFilePath,
    string? HistoryDatabasePath)
{
    public bool IsSuccess => Status == WorkspaceTrendsStatus.Passed;
}
