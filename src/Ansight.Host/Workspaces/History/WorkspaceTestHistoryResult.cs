using System.Text.Json.Serialization;

namespace Ansight.Host.Workspaces.History;

public sealed record WorkspaceTestHistoryResult(
    string HistoryDirectoryPath,
    IReadOnlyList<WorkspaceTestBatchRunHistoryEntry> Batches,
    IReadOnlyList<WorkspaceTestRunHistorySummary> Runs);
