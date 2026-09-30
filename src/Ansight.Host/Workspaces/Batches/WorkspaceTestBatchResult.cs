namespace Ansight.Host.Workspaces.Batches;

public sealed record WorkspaceTestBatchResult(
    IReadOnlyList<WorkspaceTestRunResult> Results,
    int PassedCount,
    int FailedCount,
    int SkippedCount,
    bool WasCancelled)
{
    public string? BatchRunId { get; init; }

    public string? HistoryFilePath { get; init; }

    public string? HistoryPersistenceError { get; init; }

    public bool Parallel { get; init; }
}
